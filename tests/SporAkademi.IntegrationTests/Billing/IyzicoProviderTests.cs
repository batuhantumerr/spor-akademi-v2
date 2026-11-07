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

    private sealed record CapturedRequest(Uri Uri, Dictionary<string, string> Headers, string Body);

    private sealed class StubHandler(string[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new CapturedRequest(request.RequestUri!, headers, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[_next++]) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
