using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Infrastructure.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// P2-F1 helpers: the Fake provider (DEC-347) of the API under test, its sandbox page, the browser's return and the
/// provider's webhook, and database counts read as the migrator (no tenant filter).
/// </summary>
public abstract class OnlinePaymentTest(DatabaseFixture database) : BillingTest(database)
{
    private protected FakePaymentProvider Fake => Api.Services.GetRequiredService<FakePaymentProvider>();

    /// <summary>An anonymous client that does not follow redirects, like the provider or the payer's browser.</summary>
    protected HttpClient Anonymous() => Api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected static string TokenOf(JsonElement onlinePayment)
    {
        var url = onlinePayment.GetProperty("checkoutUrl").GetString()!;
        return url[(url.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..];
    }

    /// <summary>The payer pays (or is declined) on the sandbox page; this also sends the Fake provider's webhook.</summary>
    protected async Task<HttpResponseMessage> CompleteSandboxAsync(string token, bool succeed = true)
    {
        using var client = Anonymous();
        return await client.PostAsJsonAsync("/api/v1/sandbox-checkout/complete", new { token, succeed }, Ct);
    }

    /// <summary>The browser's return from the hosted checkout: a form POST with the token.</summary>
    protected async Task<HttpResponseMessage> ReturnAsync(string token)
    {
        using var client = Anonymous();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        return await client.PostAsync(new Uri("/api/v1/payment-callbacks", UriKind.Relative), form, Ct);
    }

    protected async Task<HttpResponseMessage> WebhookAsync(string body, IReadOnlyDictionary<string, string> headers)
    {
        using var client = Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/payment-webhooks", UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>The webhook the Fake provider sends for the checkout's current state (after the sandbox page completed it).</summary>
    protected (string Body, IReadOnlyDictionary<string, string> Headers) FakeWebhook(string token) =>
        Fake.Webhook(token) ?? throw new InvalidOperationException("The checkout is not completed.");

    protected async Task<JsonElement> OnlinePaymentAsync(Guid id, HttpClient? client = null)
    {
        var response = await (client ?? Admin).GetAsync(new Uri($"/api/v1/online-payments/{id}", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    protected async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    protected Task<long> OnlinePaymentsRecordedAsync() =>
        CountAsync("SELECT count(*) FROM billing.payments WHERE method = 'Online'");
}
