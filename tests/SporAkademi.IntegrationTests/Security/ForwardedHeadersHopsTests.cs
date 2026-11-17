using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>
/// P5-06 (DEC-651): on Azure Container Apps a request passes two proxies (Caddy behind the gateway's ingress, then the
/// API's own ingress). <c>ForwardedHeaders:ForwardLimit</c> says how many hops the API peels off; every hop must still be
/// a configured proxy or network, so a client can never pick its own address.
/// </summary>
public sealed class ForwardedHeadersHopsTests(DatabaseFixture database) : IntegrationTest(database)
{
    private const string Ingress = "10.0.0.6";
    private const string Caddy = "10.0.0.5";
    private const string RealClient = "203.0.113.1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task With_the_default_limit_of_one_hop_two_proxies_make_every_client_look_like_the_inner_proxy()
    {
        using var api = Anonymous(1, new() { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/24" });
        using var http = api.CreateClient();

        (await GetAsync(http, Ingress, $"{RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // The documented gap of one hop: another client is counted as the same address (Caddy) and gets no budget of its own.
        (await GetAsync(http, Ingress, $"203.0.113.2, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task With_a_limit_of_two_each_client_behind_two_proxies_has_its_own_budget()
    {
        using var api = Anonymous(1, new() { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/24", ["ForwardedHeaders:ForwardLimit"] = "2" });
        using var http = api.CreateClient();

        (await GetAsync(http, Ingress, $"{RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(http, Ingress, $"{RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await GetAsync(http, Ingress, $"203.0.113.2, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task A_client_cannot_buy_a_fresh_budget_by_prepending_addresses_to_the_header()
    {
        using var api = Anonymous(1, new() { ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/24", ["ForwardedHeaders:ForwardLimit"] = "2" });
        using var http = api.CreateClient();

        (await GetAsync(http, Ingress, $"198.51.100.1, {RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // Only the last two hops are taken: the spoofed first entry changes nothing.
        (await GetAsync(http, Ingress, $"198.51.100.2, {RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task A_higher_limit_never_trusts_an_untrusted_hop_in_the_chain()
    {
        using var api = Anonymous(1, new() { ["ForwardedHeaders:KnownProxies:0"] = Ingress, ["ForwardedHeaders:KnownProxies:1"] = Caddy, ["ForwardedHeaders:ForwardLimit"] = "5" });
        using var http = api.CreateClient();

        // The first address that is not a configured proxy is the client; the entries before it are never read.
        (await GetAsync(http, Ingress, $"198.51.100.1, {RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(http, Ingress, $"198.51.100.2, {RealClient}, {Caddy}")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        // And a source that is not a configured proxy is not believed at all, whatever the limit.
        (await GetAsync(http, "198.51.100.7", "203.0.113.50")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(http, "198.51.100.7", "203.0.113.51")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("two")]
    [InlineData("11")]
    [Trait("Story", "P5-06")]
    public void An_invalid_forward_limit_stops_the_start(string value)
    {
        using var api = Anonymous(1, new() { ["ForwardedHeaders:KnownProxies:0"] = Ingress, ["ForwardedHeaders:ForwardLimit"] = value });

        Should.Throw<Exception>(() => api.CreateClient()).ToString().ShouldContain("ForwardLimit");
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string address, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sports").From(address, forwardedFor);
        return await http.SendAsync(request, Ct);
    }

    private WebApplicationFactory<Program> Anonymous(int permits, Dictionary<string, string?> settings) =>
        Api.WithWebHostBuilder(builder =>
        {
            RemoteAddress.Enable(builder);
            settings["RateLimiting:AnonymousPermitsPerMinute"] = permits.ToString(CultureInfo.InvariantCulture);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        });
}
