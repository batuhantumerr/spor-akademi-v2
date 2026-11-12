using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Billing;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// DEC-345: the iyzico adapter against a stubbed HTTP handler (no network, no real keys). It checks the documented
/// IYZWSv2 signing, the Checkout Form requests and how responses map to provider states.
/// </summary>
[Trait("Roadmap", "P2-F1")]
public sealed class IyzicoProviderTests
{
    private const string ApiKey = "test-api-test-api-test";
    private const string SecretKey = "test-secret-test-secret";
    private static readonly Guid OnlinePaymentId = Guid.Parse("0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IyzicoPaymentProvider Provider, StubHandler Handler) Provider(params string[] responses)
    {
        var handler = new StubHandler(responses);
        var options = Options.Create(new PaymentOptions
        {
            Iyzico = new IyzicoOptions { ApiKey = ApiKey, SecretKey = SecretKey, CallbackUrl = "https://app.example.test/api/v1/payment-callbacks" },
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero));
        return (new IyzicoPaymentProvider(new StubFactory(handler), options, clock), handler);
    }

    [Fact]
    public async Task Opening_a_checkout_signs_the_request_as_documented_and_returns_the_hosted_page()
    {
        var (provider, handler) = Provider("""{"status":"success","token":"tok-123","paymentPageUrl":"https://sandbox-cpp.iyzipay.com?token=tok-123"}""");

        var opening = await provider.OpenCheckoutAsync(
            new CheckoutRequest(OnlinePaymentId, Guid.CreateVersion7(), Guid.CreateVersion7(), "2027-01", Money.Try(1000m)), Ct);

        opening.Token.ShouldBe("tok-123");
        opening.CheckoutUrl.ShouldBe("https://sandbox-cpp.iyzipay.com?token=tok-123");
        opening.ExpiresAt.ShouldBe(new DateTimeOffset(2027, 1, 14, 12, 30, 0, TimeSpan.Zero));

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://sandbox-api.iyzipay.com/payment/iyzipos/checkoutform/initialize/auth/ecom"));
        var random = request.Headers["x-iyzi-rnd"];
        request.Headers["Authorization"].ShouldStartWith("IYZWSv2 ");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers["Authorization"]["IYZWSv2 ".Length..]));
        var expectedSignature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SecretKey),
            Encoding.UTF8.GetBytes(random + "/payment/iyzipos/checkoutform/initialize/auth/ecom" + request.Body)));
        decoded.ShouldBe($"apiKey:{ApiKey}&randomKey:{random}&signature:{expectedSignature}");

        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("conversationId").GetString().ShouldBe(OnlinePaymentId.ToString());
        body.RootElement.GetProperty("price").GetString().ShouldBe("1000.00");
        body.RootElement.GetProperty("paidPrice").GetString().ShouldBe("1000.00");
        body.RootElement.GetProperty("currency").GetString().ShouldBe("TRY");
        body.RootElement.GetProperty("callbackUrl").GetString().ShouldBe("https://app.example.test/api/v1/payment-callbacks");
        body.RootElement.GetProperty("basketItems")[0].GetProperty("itemType").GetString().ShouldBe("VIRTUAL");
        // DEC-345: sandbox placeholders only, never a student's or guardian's data.
        body.RootElement.GetProperty("buyer").GetProperty("name").GetString().ShouldBe("Sandbox");
    }

    [Theory]
    [InlineData("http://sandbox-cpp.iyzipay.com?token=tok-123")]
    [InlineData("https://sandbox-cpp.iyzipay.com.example.test?token=tok-123")]
    [InlineData("https://evil.example.test/iyzipay.com")]
    [InlineData("https://sandbox-cpp.iyzipay.com:8443/?token=tok-123")]
    [InlineData("/relative")]
    public async Task A_payment_page_that_is_not_iyzicos_https_page_is_refused(string pageUrl)
    {
        var (provider, _) = Provider($$"""{"status":"success","token":"tok-123","paymentPageUrl":"{{pageUrl}}"}""");

        var error = await Should.ThrowAsync<PaymentProviderException>(() => provider.OpenCheckoutAsync(
            new CheckoutRequest(OnlinePaymentId, Guid.CreateVersion7(), Guid.CreateVersion7(), "2027-01", Money.Try(10m)), Ct));

        error.Message.ShouldBe("UntrustedPaymentPage");
    }

    [Fact]
    public async Task A_refused_initialize_is_a_provider_error_with_only_the_error_code()
    {
        var (provider, _) = Provider("""{"status":"failure","errorCode":"10051","errorMessage":"Kart limiti yetersiz, Ali Veli"}""");

        var error = await Should.ThrowAsync<PaymentProviderException>(() => provider.OpenCheckoutAsync(
            new CheckoutRequest(OnlinePaymentId, Guid.CreateVersion7(), Guid.CreateVersion7(), "2027-01", Money.Try(10m)), Ct));

        error.Message.ShouldBe("Iyzico10051");
    }

    [Theory]
    [InlineData("""{"status":"success","paymentStatus":"SUCCESS","fraudStatus":1,"paymentId":"22416035","conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70","paidPrice":1000.0,"currency":"TRY"}""", ProviderPaymentOutcome.Succeeded)]
    [InlineData("""{"status":"success","paymentStatus":"SUCCESS","fraudStatus":0,"paymentId":"22416035","conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70","paidPrice":1000.0,"currency":"TRY"}""", ProviderPaymentOutcome.Pending)]
    [InlineData("""{"status":"success","paymentStatus":"SUCCESS","fraudStatus":-1,"conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70"}""", ProviderPaymentOutcome.Failed)]
    [InlineData("""{"status":"success","paymentStatus":"FAILURE","errorCode":"10005","conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70"}""", ProviderPaymentOutcome.Failed)]
    [InlineData("""{"status":"success","paymentStatus":"INIT_THREEDS","conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70"}""", ProviderPaymentOutcome.Pending)]
    public async Task The_checkout_detail_maps_to_a_provider_state(string response, ProviderPaymentOutcome outcome)
    {
        var (provider, handler) = Provider(response);

        var state = await provider.RetrieveAsync("tok-123", Ct);

        state.Outcome.ShouldBe(outcome);
        state.ConversationId.ShouldBe(OnlinePaymentId);
        handler.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/payment/iyzipos/checkoutform/auth/ecom/detail");
        if (outcome == ProviderPaymentOutcome.Succeeded)
        {
            state.ProviderPaymentId.ShouldBe("22416035");
            state.PaidAmount.ShouldBe(Money.Try(1000m));
        }
    }

    [Fact]
    public async Task An_undecidable_detail_is_retried_later_not_failed()
    {
        var (provider, _) = Provider("""{"status":"failure","errorCode":"5000"}""");

        await Should.ThrowAsync<PaymentProviderException>(() => provider.RetrieveAsync("tok-123", Ct));
    }

    [Fact]
    public void Hosted_checkout_webhooks_are_verified_with_the_documented_signature()
    {
        var (provider, _) = Provider();
        var webhook = new HostedWebhook("CHECKOUT_FORM_AUTH", "22416035", "tok-123", OnlinePaymentId.ToString(), "SUCCESS");
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SecretKey),
            Encoding.UTF8.GetBytes(SecretKey + "CHECKOUT_FORM_AUTH" + "22416035" + "tok-123" + OnlinePaymentId + "SUCCESS")));
        var body = """{"paymentConversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f70","merchantId":"1","token":"tok-123","status":"SUCCESS","iyziReferenceCode":"r","iyziEventType":"CHECKOUT_FORM_AUTH","iyziEventTime":1,"iyziPaymentId":22416035}""";

        IyzicoProtocol.WebhookSignature(SecretKey, webhook).ShouldBe(expected);
        var verified = provider.VerifyNotification(body, new Dictionary<string, string> { ["x-iyz-signature-v3"] = expected.ToUpperInvariant() });

        verified.ShouldNotBeNull();
        verified.Token.ShouldBe("tok-123");
        verified.EventKey.ShouldBe("CHECKOUT_FORM_AUTH:22416035:SUCCESS:tok-123");
        provider.VerifyNotification(body.Replace("SUCCESS", "FAILURE", StringComparison.Ordinal), new Dictionary<string, string> { ["X-IYZ-SIGNATURE-V3"] = expected }).ShouldBeNull();
        provider.VerifyNotification("not json", new Dictionary<string, string> { ["X-IYZ-SIGNATURE-V3"] = expected }).ShouldBeNull();
    }

    [Fact]
    [Trait("Roadmap", "P3-13")]
    public async Task A_refund_is_the_signed_amount_based_refund_of_the_full_charge()
    {
        var refundId = Guid.Parse("0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f71");
        var (provider, handler) = Provider("""{"status":"success","conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f71","paymentId":"22416035","price":1000.0,"currency":"TRY","hostReference":"host-77"}""");

        var result = await provider.RefundAsync(new RefundRequest(refundId, "22416035", Money.Try(1000m)), Ct);

        result.Outcome.ShouldBe(ProviderRefundOutcome.Refunded);
        result.ProviderRefundId.ShouldBe("host-77");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.ShouldBe(new Uri("https://sandbox-api.iyzipay.com/v2/payment/refund"));
        var random = request.Headers["x-iyzi-rnd"];
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers["Authorization"]["IYZWSv2 ".Length..]));
        var expectedSignature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SecretKey),
            Encoding.UTF8.GetBytes(random + "/v2/payment/refund" + request.Body)));
        decoded.ShouldBe($"apiKey:{ApiKey}&randomKey:{random}&signature:{expectedSignature}");

        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("conversationId").GetString().ShouldBe(refundId.ToString());
        body.RootElement.GetProperty("paymentId").GetString().ShouldBe("22416035");
        body.RootElement.GetProperty("price").GetString().ShouldBe("1000.00");
        body.RootElement.GetProperty("currency").GetString().ShouldBe("TRY");
        // A fixed placeholder, never a user's address; and no reason or names reach the provider.
        body.RootElement.GetProperty("ip").GetString().ShouldBe("127.0.0.1");
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["locale", "conversationId", "paymentId", "price", "currency", "ip"]);
    }

    [Fact]
    [Trait("Roadmap", "P3-13")]
    public async Task A_refused_refund_is_checked_against_the_payment_detail_with_only_the_error_code()
    {
        var (provider, _) = Provider("""{"status":"failure","errorCode":"5093","errorMessage":"İade süresi geçti, Ali Veli"}""");

        var result = await provider.RefundAsync(new RefundRequest(Guid.CreateVersion7(), "22416035", Money.Try(10m)), Ct);

        // DEC-418: a refusal can mean "already refunded"; the payment detail decides.
        result.Outcome.ShouldBe(ProviderRefundOutcome.Verify);
        result.FailureCode.ShouldBe("Iyzico5093");
    }

    [Theory]
    [Trait("Roadmap", "P3-13")]
    [InlineData("503:{}")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"status":"success","conversationId":"0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f99"}""")]
    public async Task A_server_error_or_an_unreadable_refund_answer_is_an_unknown_outcome(string response)
    {
        var (provider, _) = Provider(response);

        await Should.ThrowAsync<PaymentProviderException>(() =>
            provider.RefundAsync(new RefundRequest(Guid.Parse("0192f0c5-3b7a-7c2e-9f1a-2b3c4d5e6f71"), "22416035", Money.Try(10m)), Ct));
    }

    [Theory]
    [Trait("Roadmap", "P3-13")]
    [InlineData("TOTAL_REFUNDED", true)]
    [InlineData("NOT_REFUNDED", false)]
    public async Task The_refund_status_check_is_a_signed_get_of_the_payment_detail(string status, bool refunded)
    {
        var (provider, handler) = Provider($$"""{"status":"success","payments":[{"paymentId":"22416035","paymentRefundStatus":"{{status}}"}]}""");

        var check = await provider.CheckRefundAsync(new RefundRequest(Guid.CreateVersion7(), "22416035", Money.Try(10m)), Ct);

        check.Refunded.ShouldBe(refunded);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.PathAndQuery.ShouldBe("/v2/reporting/payment/details?paymentId=22416035");
        request.Body.ShouldBeEmpty();
        var random = request.Headers["x-iyzi-rnd"];
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers["Authorization"]["IYZWSv2 ".Length..]));
        var expectedSignature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SecretKey),
            Encoding.UTF8.GetBytes(random + "/v2/reporting/payment/details?paymentId=22416035")));
        decoded.ShouldBe($"apiKey:{ApiKey}&randomKey:{random}&signature:{expectedSignature}");
    }

    [Theory]
    [Trait("Roadmap", "P3-13")]
    [InlineData("""{"status":"success","payments":[{"paymentId":"22416035","paymentRefundStatus":"PARTIAL_REFUNDED"}]}""")]
    [InlineData("""{"status":"success","payments":[]}""")]
    [InlineData("""{"status":"failure","errorCode":"1"}""")]
    [InlineData("503:{}")]
    public async Task An_inconclusive_refund_status_is_not_a_result(string response)
    {
        var (provider, _) = Provider(response);

        await Should.ThrowAsync<PaymentProviderException>(() =>
            provider.CheckRefundAsync(new RefundRequest(Guid.CreateVersion7(), "22416035", Money.Try(10m)), Ct));
    }

    private sealed record CapturedRequest(Uri Uri, Dictionary<string, string> Headers, string Body);

    private sealed class StubHandler(string[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!, headers, body));
            // "503:" prefixes a server error answer.
            var response = responses[_next++];
            return response.StartsWith("503:", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(response[4..]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
