using System.Net;
using Microsoft.AspNetCore.Hosting;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>
/// NFR-SEC-13 (P4-01, DEC-500): every API response carries the security headers, whatever produced it (an endpoint,
/// a problem from the framework, an unknown route). The API serves JSON only, so its policy allows nothing; the job
/// panel is the one HTML page and gets a policy of its own. NFR-SEC-02: HSTS only in Production and only over HTTPS.
/// </summary>
public sealed class SecurityHeadersTests(DatabaseFixture database) : IntegrationTest(database)
{
    private const string ApiPolicy = "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Paths() => new()
    {
        "/health/live",
        "/api/v1/students",
        "/api/v1/no-such-route",
        "/hubs/notifications/negotiate",
    };

    [Theory]
    [Trait("Story", "P4-01")]
    [MemberData(nameof(Paths))]
    public async Task Api_responses_carry_the_security_headers(string path)
    {
        var response = await Client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Referrer-Policy").ShouldBe("no-referrer");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "Content-Security-Policy").ShouldBe(ApiPolicy);
    }

    [Fact]
    [Trait("Story", "P4-01")]
    public async Task Signed_in_and_error_responses_carry_them_too()
    {
        Client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));

        var invalid = await Client.PostAsync(new Uri("/api/v1/sports", UriKind.Relative), JsonContent("{}"), Ct);

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Header(invalid, "Content-Security-Policy").ShouldBe(ApiPolicy);
        Header(invalid, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact]
    [Trait("Story", "P4-01")]
    public async Task The_job_panel_gets_a_policy_for_its_own_pages()
    {
        var response = await Client.GetAsync(new Uri("/jobs", UriKind.Relative), Ct);

        var policy = Header(response, "Content-Security-Policy");
        policy.ShouldContain("default-src 'self'");
        policy.ShouldContain("script-src 'self'");
        policy.ShouldNotContain("'unsafe-eval'");
        policy.ShouldContain("frame-ancestors 'none'");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
    }

    [Fact]
    [Trait("Story", "P4-01")]
    public async Task Outside_production_there_is_no_hsts_even_over_https()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/health/live");

        var response = await Client.SendAsync(request, Ct);

        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    [Trait("Story", "P4-01")]
    public async Task Production_sends_hsts_over_https_only()
    {
        using var production = Api.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();

        using var https = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/health/live");
        var secure = await client.SendAsync(https, Ct);
        using var http = new HttpRequestMessage(HttpMethod.Get, "http://api.example.test/health/live");
        var plain = await client.SendAsync(http, Ct);

        Header(secure, "Strict-Transport-Security").ShouldBe("max-age=31536000");
        plain.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    [Trait("Story", "P4-01")]
    public async Task Behind_a_known_proxy_production_sends_hsts_when_the_client_used_https()
    {
        using var production = Api.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            RemoteAddress.Enable(builder);
            builder.UseSetting("ForwardedHeaders:KnownProxies:0", "10.0.0.5");
        });
        using var client = production.CreateClient();

        // TLS ends at the proxy: the API sees plain HTTP, and the scheme only in X-Forwarded-Proto.
        using var viaProxy = new HttpRequestMessage(HttpMethod.Get, "http://api.example.test/health/live").From("10.0.0.5", "203.0.113.1", "https");
        var secure = await client.SendAsync(viaProxy, Ct);
        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "http://api.example.test/health/live").From("198.51.100.7", "203.0.113.1", "https");
        var direct = await client.SendAsync(spoofed, Ct);

        Header(secure, "Strict-Transport-Security").ShouldBe("max-age=31536000");
        direct.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : throw new ShouldAssertException($"{name} is missing on {(int)response.StatusCode} {response.RequestMessage?.RequestUri}.");

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
