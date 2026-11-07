using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Billing;
using SporAkademi.Infrastructure.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>DEC-340: the provider is optional configuration; existing setups keep working without any new key.</summary>
[Trait("Roadmap", "P2-F1")]
public sealed class OnlinePaymentConfigurationTests(DatabaseFixture database) : BillingTest(database)
{
    private WebApplicationFactory<Program> With(params (string Key, string? Value)[] settings) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))));

    [Fact]
    public void Without_configuration_the_fake_provider_is_used_outside_production()
    {
        Api.Services.GetRequiredService<IPaymentProviders>().Active.ShouldBeOfType<FakePaymentProvider>();
    }

    [Fact]
    public async Task With_the_provider_switched_off_online_payment_is_disabled_and_the_provider_endpoints_answer_not_found()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        using var api = With(("Payments:Provider", "None"));
        using var admin = api.CreateClient();
        admin.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/online-payments", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enrollmentId = enrollment, period = "2027-01" }),
        };
        request.Headers.Add("Idempotency-Key", "kapali-anahtar-1");

        var started = await admin.SendAsync(request, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(started)).ShouldBe("OnlinePayment.Disabled");

        using var anonymous = api.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/sandbox-checkout/complete", new { token = "fake-x", succeed = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.PostAsync(new Uri("/api/v1/payment-webhooks", UriKind.Relative), new StringContent("{}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Production", null, null, null, true)]
    [InlineData("Production", "Fake", null, null, false)]
    [InlineData("Development", "Iyzico", null, null, false)]
    [InlineData("Development", "", "sandbox-api-key", "https://app.example.test/api/v1/payment-callbacks", true)]
    [InlineData("Development", "Iyzico", "sandbox-api-key", null, false)]
    [InlineData("Development", "Paypal", null, null, false)]
    public void The_payments_section_is_validated_at_start(string environment, string? provider, string? apiKey, string? callbackUrl, bool valid)
    {
        var options = new PaymentOptions
        {
            Provider = provider,
            Iyzico = new IyzicoOptions { ApiKey = apiKey, SecretKey = apiKey is null ? null : "sandbox-secret-key", CallbackUrl = callbackUrl },
        };

        var result = new PaymentOptionsValidator(new FixedEnvironment(environment)).Validate(null, options);

        result.Succeeded.ShouldBe(valid, result.FailureMessage);
    }

    [Theory]
    [InlineData("Development", null, "Fake")]
    [InlineData("Testing", "", "Fake")]
    [InlineData("Staging", null, null)]
    [InlineData("Demo", "", null)]
    [InlineData("Production", null, null)]
    [InlineData("Staging", "Fake", "Fake")]
    [InlineData("Development", "None", null)]
    public void The_fake_provider_is_the_default_only_in_development_and_testing(string environment, string? provider, string? expected)
    {
        var options = new PaymentOptions { Provider = provider };
        var host = new FixedEnvironment(environment);

        new PaymentOptionsValidator(host).Validate(null, options).Succeeded.ShouldBeTrue();
        options.Resolve(host).ShouldBe(expected);
    }

    [Fact]
    public void A_live_iyzico_url_is_refused()
    {
        var options = new PaymentOptions
        {
            Iyzico = new IyzicoOptions { BaseUrl = "https://api.iyzipay.com", ApiKey = "k", SecretKey = "s", CallbackUrl = "https://app.example.test/cb" },
        };

        var result = new PaymentOptionsValidator(new FixedEnvironment("Development")).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("sandbox only");
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "SporAkademi.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
