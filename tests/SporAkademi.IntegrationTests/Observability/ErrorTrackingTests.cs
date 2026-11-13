using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Observability;

/// <summary>
/// P4-07 (DEC-556): Sentry error tracking with a fake transport (nothing leaves the test). Without a DSN the SDK sends
/// nothing; with one, an unexpected error is reported with its exception type and stack, but without the exception
/// message, request body, cookies, headers, query string or user.
/// </summary>
[Trait("Story", "P4-07")]
public sealed class ErrorTrackingTests(DatabaseFixture database) : IntegrationTest(database)
{
    private const string Email = "hata.gizli@example.com";
    private const string QuerySecret = "query-secret-p407";
    private const string CookieSecret = "cookie-secret-p407";
    private const string HeaderSecret = "header-secret-p407";
    private const string FakeDsn = "https://synthetic-test-key@sentry.invalid/1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_dsn_nothing_is_sent()
    {
        var transport = new CapturingTransport();
        using var api = SentryApi(transport, dsn: null);
        using var client = api.CreateClient();
        var token = AccessToken;

        (await ThrowAsync(client, token)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await api.Services.GetRequiredService<IHub>().FlushAsync(TimeSpan.FromSeconds(5));

        api.Services.GetRequiredService<IHub>().IsEnabled.ShouldBeFalse();
        transport.Envelopes.ShouldBeEmpty();
    }

    /// <summary>Only <c>Sentry:Dsn</c> sends to Sentry; the SDK's own <c>SENTRY_DSN</c> fallback sends nothing (DEC-556).</summary>
    [Fact]
    public async Task The_sentry_dsn_environment_variable_alone_sends_nothing()
    {
        var previous = Environment.GetEnvironmentVariable("SENTRY_DSN");
        Environment.SetEnvironmentVariable("SENTRY_DSN", FakeDsn);
        try
        {
            var transport = new CapturingTransport();
            using var api = SentryApi(transport, dsn: null);
            using var client = api.CreateClient();

            (await ThrowAsync(client, AccessToken)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            await api.Services.GetRequiredService<IHub>().FlushAsync(TimeSpan.FromSeconds(5));

            // The SDK itself takes SENTRY_DSN and starts; every event is dropped before it reaches the transport.
            transport.Envelopes.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SENTRY_DSN", previous);
        }
    }

    [Fact]
    public async Task With_a_dsn_an_unexpected_error_is_sent_without_personal_data()
    {
        var transport = new CapturingTransport();
        using var api = SentryApi(transport, FakeDsn);
        using var client = api.CreateClient();
        var token = AccessToken;

        (await ThrowAsync(client, token)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await api.Services.GetRequiredService<IHub>().FlushAsync(TimeSpan.FromSeconds(5));

        var sent = string.Join('\n', transport.Envelopes);
        // Proof that the error was reported, with what identifies it.
        sent.ShouldContain("\"type\":\"event\"");
        sent.ShouldContain("System.InvalidOperationException");
        sent.ShouldContain("/test/throw");
        foreach (var value in new[] { Email, "hata.gizli", QuerySecret, CookieSecret, HeaderSecret, "Could not process", token, token.Split('.')[2] })
        {
            sent.ShouldNotContain(value, Case.Insensitive, $"'{value}' was sent to Sentry.");
        }
    }

    /// <summary>The fallback policy needs a signed-in user, so the request carries an access token (which must not leak either).</summary>
    private string AccessToken => TestTokens.Create(Guid.CreateVersion7(), "PlatformAdmin", null, Api.Clock.GetUtcNow());

    private static async Task<HttpResponseMessage> ThrowAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/test/throw?token={QuerySecret}")
        {
            Content = JsonContent.Create(new { email = Email }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Cookie", $"sa_refresh={CookieSecret}");
        request.Headers.Add("X-Api-Secret", HeaderSecret);
        return await client.SendAsync(request, Ct);
    }

    private WebApplicationFactory<Program> SentryApi(CapturingTransport transport, string? dsn) =>
        Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Sentry:Dsn"] = dsn }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new DuplicateUserEndpoint());
                services.PostConfigure<SentryAspNetCoreOptions>(options => options.Transport = transport);
            });
        });

    private sealed class CapturingTransport : ITransport
    {
        public ConcurrentQueue<string> Envelopes { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            Envelopes.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}
