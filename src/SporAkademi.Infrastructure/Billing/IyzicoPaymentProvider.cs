using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Billing;

/// <summary>
/// iyzico Checkout Form over plain <see cref="HttpClient"/> (DEC-345): initialize opens the hosted page, the detail
/// query (by token) is the source of truth, webhooks are verified with <c>X-IYZ-SIGNATURE-V3</c>. Sandbox only.
/// iyzico requires buyer and address fields; until the parent portal (Phase 3) decides what payer data may be sent
/// (KVKK), fixed sandbox placeholders are used, never the student's or guardian's data.
/// </summary>
internal sealed class IyzicoPaymentProvider(IHttpClientFactory httpClients, IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentProvider
{
    public const string HttpClientName = "iyzico";
    public const string InitializePath = "/payment/iyzipos/checkoutform/initialize/auth/ecom";
    public const string RetrievePath = "/payment/iyzipos/checkoutform/auth/ecom/detail";
    public const string RefundPath = "/v2/payment/refund";
    public const string RefundStatusPath = "/v2/reporting/payment/details";

    /// <summary>iyzico requires the refund requester's IP; a fixed placeholder, never a user's address (KVKK).</summary>
    public const string RefundRequesterIp = "127.0.0.1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Where iyzico's refund answer carries its reference, in order of preference.</summary>
    private static readonly string[] RefundReferenceFields = ["refundHostReference", "hostReference", "paymentTransactionId"];

    public string Name => PaymentOptions.IyzicoName;

    private IyzicoOptions Settings => options.Value.Iyzico;

    public async Task<CheckoutOpening> OpenCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var price = request.Amount.ToAmountString();
        var conversationId = request.OnlinePaymentId.ToString();
        var address = new { contactName = "Sandbox Odeyen", city = "Istanbul", country = "Turkey", address = "Sandbox" };
        var body = new
        {
            locale = "tr",
            conversationId,
            price,
            paidPrice = price,
            currency = request.Amount.Currency,
            basketId = conversationId,
            paymentGroup = "PRODUCT",
            callbackUrl = Settings.CallbackUrl,
            enabledInstallments = new[] { 1 },
            buyer = new
            {
                id = request.StudentId.ToString(),
                name = "Sandbox",
                surname = "Odeyen",
                identityNumber = "11111111111",
                email = "sandbox-odeyen@example.com",
                gsmNumber = "+905350000000",
                registrationAddress = "Sandbox",
                city = "Istanbul",
                country = "Turkey",
            },
            shippingAddress = address,
            billingAddress = address,
            basketItems = new[]
            {
                new { id = $"{request.EnrollmentId:N}-{request.Period}", name = $"Aidat {request.Period}", category1 = "Aidat", itemType = "VIRTUAL", price },
            },
        };

        using var response = await PostAsync(InitializePath, body, cancellationToken);
        var root = response.RootElement;
        var token = IyzicoProtocol.Text(root, "token");
        var pageUrl = IyzicoProtocol.Text(root, "paymentPageUrl");
        if (!IsSuccess(root) || token.Length == 0)
        {
            throw new PaymentProviderException(ErrorCode(root));
        }

        // The link is shared with the payer: it must be iyzico's own HTTPS page, never anything else the response says.
        if (!IsIyzicoPage(pageUrl))
        {
            throw new PaymentProviderException("UntrustedPaymentPage");
        }

        return new CheckoutOpening(token, pageUrl, clock.GetUtcNow() + options.Value.CheckoutLifetime);
    }

    public async Task<ProviderPaymentState> RetrieveAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(RetrievePath, new { locale = "tr", token }, cancellationToken);
        var root = response.RootElement;
        if (!IsSuccess(root))
        {
            // Not decidable (unknown token, provider error): the caller retries later, nothing is marked failed.
            throw new PaymentProviderException(ErrorCode(root));
        }

        var conversation = Guid.TryParse(IyzicoProtocol.Text(root, "conversationId"), out var id) ? id : (Guid?)null;
        var paymentStatus = IyzicoProtocol.Text(root, "paymentStatus");
        var fraudStatus = IyzicoProtocol.Text(root, "fraudStatus");
        if (string.Equals(paymentStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            if (fraudStatus != "1")
            {
                // In fraud review (0) stays pending; rejected (-1) is a failure.
                return fraudStatus == "-1"
                    ? new ProviderPaymentState(ProviderPaymentOutcome.Failed, conversation, null, null, "FraudRejected")
                    : new ProviderPaymentState(ProviderPaymentOutcome.Pending, conversation, null, null, null);
            }

            var paid = decimal.TryParse(IyzicoProtocol.Text(root, "paidPrice"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                ? Money.Create(amount, IyzicoProtocol.Text(root, "currency"))
                : Result.Failure<Money>(Money.Invalid);
            var paymentId = IyzicoProtocol.Text(root, "paymentId");
            return paid.IsSuccess && paymentId.Length > 0
                ? new ProviderPaymentState(ProviderPaymentOutcome.Succeeded, conversation, paymentId, paid.Value, null)
                : throw new PaymentProviderException("UnreadableDetail");
        }

        return string.Equals(paymentStatus, "FAILURE", StringComparison.OrdinalIgnoreCase)
            ? new ProviderPaymentState(ProviderPaymentOutcome.Failed, conversation, null, null, ErrorCode(root, "PaymentFailed"))
            : new ProviderPaymentState(ProviderPaymentOutcome.Pending, conversation, null, null, null);
    }

    /// <summary>
    /// P3-13 (DEC-424): iyzico's amount-based refund (<c>/v2/payment/refund</c>) of the full charge by its payment id; our
    /// refund id is the conversation id. <c>status=success</c> is a refund; <c>status=failure</c> with an error code is a
    /// definite refusal; a server error, timeout or unreadable answer is an unknown outcome (the request stays pending).
    /// iyzico wants the requester's IP; a fixed placeholder is sent, never a user's address.
    /// </summary>
    public async Task<ProviderRefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var conversationId = request.RefundId.ToString();
        var body = new
        {
            locale = "tr",
            conversationId,
            paymentId = request.ProviderPaymentId,
            price = request.Amount.ToAmountString(),
            currency = request.Amount.Currency,
            ip = RefundRequesterIp,
        };

        using var response = await PostAsync(RefundPath, body, cancellationToken);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object || IyzicoProtocol.Text(root, "status").Length == 0)
        {
            throw new PaymentProviderException("UnreadableRefund");
        }

        if (!IsSuccess(root))
        {
            // DEC-418: iyzico refuses a second refund of a payment ("already refunded", "exceeds the refundable amount")
            // with the same failure shape as a real refusal, and its codes are not verified against the sandbox yet; the
            // payment detail decides every refusal.
            return ProviderRefundResult.Verify(ErrorCode(root, "RefundDeclined"));
        }

        var echoed = IyzicoProtocol.Text(root, "conversationId");
        if (echoed.Length > 0 && !string.Equals(echoed, conversationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new PaymentProviderException("RefundConversationMismatch");
        }

        var reference = RefundReferenceFields
            .Select(name => IyzicoProtocol.Text(root, name))
            .FirstOrDefault(value => value.Length > 0);
        return ProviderRefundResult.Refunded(reference ?? $"iyzico-refund-{request.RefundId:N}");
    }

    /// <summary>
    /// DEC-418: the payment's refund status from iyzico's reporting API (<c>GET /v2/reporting/payment/details?paymentId=</c>,
    /// signed over the path without its query and an empty body, like the official SDKs): <c>paymentRefundStatus=TOTAL_REFUNDED</c> means the money is
    /// back, <c>NOT_REFUNDED</c> that it is not. Anything else (partial refund, unreadable or failed answer) is not
    /// conclusive and throws, so the request stays pending for a person to check.
    /// </summary>
    public async Task<ProviderRefundCheck> CheckRefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = RefundStatusPath + "?paymentId=" + Uri.EscapeDataString(request.ProviderPaymentId);
        using var response = await SendAsync(HttpMethod.Get, path, string.Empty, cancellationToken);
        var root = response.RootElement;
        if (!IsSuccess(root) || !root.TryGetProperty("payments", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new PaymentProviderException(ErrorCode(root, "UnreadableRefundStatus"));
        }

        var payment = list.EnumerateArray().FirstOrDefault(p => IyzicoProtocol.Text(p, "paymentId") == request.ProviderPaymentId);
        return (payment.ValueKind == JsonValueKind.Object ? IyzicoProtocol.Text(payment, "paymentRefundStatus") : string.Empty) switch
        {
            "TOTAL_REFUNDED" => new ProviderRefundCheck(true, $"iyzico-refund-{request.RefundId:N}"),
            "NOT_REFUNDED" => new ProviderRefundCheck(false, null),
            _ => throw new PaymentProviderException("InconclusiveRefundStatus"),
        };
    }

    public ProviderNotification? VerifyNotification(string body, IReadOnlyDictionary<string, string> headers) =>
        Settings.SecretKey is { Length: > 0 } secret && IyzicoProtocol.VerifyWebhook(secret, body, headers) is { } webhook
            ? new ProviderNotification(webhook.Token, webhook.EventKey)
            : null;

    private Task<JsonDocument> PostAsync(string path, object payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(payload, Json), cancellationToken);

    /// <summary>A signed call; for a <c>GET</c> the body is empty and the signed path stops at the query (as the SDKs do).</summary>
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string body, CancellationToken cancellationToken)
    {
        var randomKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Settings.BaseUrl), path));
        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation("Authorization", IyzicoProtocol.Authorization(Settings.ApiKey!, Settings.SecretKey!, randomKey, IyzicoProtocol.SignedPath(path), body));
        request.Headers.Add(IyzicoProtocol.RandomHeader, randomKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = httpClients.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Settings.Timeout);
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if ((int)response.StatusCode >= 500)
            {
                throw new PaymentProviderException($"Http{(int)response.StatusCode}");
            }

            // iyzico answers business errors with 4xx and an error body; both are read the same way.
            return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PaymentProviderException(exception.GetType().Name, exception);
        }
    }

    internal static bool IsIyzicoPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var page)
        && page.Scheme == Uri.UriSchemeHttps
        && page.IsDefaultPort
        && (string.Equals(page.Host, "iyzipay.com", StringComparison.OrdinalIgnoreCase)
            || page.Host.EndsWith(".iyzipay.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsSuccess(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && string.Equals(IyzicoProtocol.Text(root, "status"), "success", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only iyzico's error code (a number or short token), never its free-text message.</summary>
    private static string ErrorCode(JsonElement root, string fallback = "ProviderError")
    {
        var code = root.ValueKind == JsonValueKind.Object ? IyzicoProtocol.Text(root, "errorCode") : string.Empty;
        return code.Length is > 0 and <= 32 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? "Iyzico" + code : fallback;
    }
}
