using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>DEC-054: the admin app and the API share one origin, so the API sends no CORS headers to anyone.</summary>
public sealed class CorsTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string ForeignOrigin = "https://evil.example";

    public static TheoryData<string, string> Requests() => new()
    {
        { "GET", "/health/live" },
        { "POST", "/api/v1/auth/login" },
        { "POST", "/api/v1/auth/refresh" },
        { "GET", "/api/v1/me" },
        { "GET", "/api/v1/students" },
        { "OPTIONS", "/api/v1/auth/login" },
        { "OPTIONS", "/api/v1/students" },
    };

    [Fact]
    public void No_cors_services_are_registered()
    {
        Api.Services.GetService<ICorsService>().ShouldBeNull();
        Api.Services.GetService<ICorsPolicyProvider>().ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Foreign_origins_get_no_cors_headers(string method, string path)
    {
        using var request = CrossOriginRequest(method, path, ForeignOrigin);

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.NoContent, "A 204 to a preflight would hint at CORS handling.");
        CorsHeadersOf(response).ShouldBeEmpty();
    }

    /// <summary>
    /// Positive control for the test above: the same requests and the same header check, against a host where CORS
    /// is switched on for the foreign origin, do find the headers. Without this the negative test could pass
    /// because the check looks in the wrong place.
    /// </summary>
    [Fact]
    public async Task The_check_finds_cors_headers_when_cors_is_enabled()
    {
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
                .WithOrigins(ForeignOrigin)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
            services.AddSingleton<IStartupFilter, CorsFirstStartupFilter>();
        }));
        using var client = api.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var preflight = CrossOriginRequest("OPTIONS", "/api/v1/auth/login", ForeignOrigin);
        var preflightResponse = await client.SendAsync(preflight, ct);
        preflightResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        CorsHeadersOf(preflightResponse).ShouldContain("Access-Control-Allow-Origin");
        preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ForeignOrigin);

        using var actual = CrossOriginRequest("POST", "/api/v1/auth/login", ForeignOrigin);
        var actualResponse = await client.SendAsync(actual, ct);
        actualResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        actualResponse.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ForeignOrigin);
        actualResponse.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
    }

    /// <summary>
    /// Positive control for DEC-054 itself: the admin app's origin is the API's own origin, and requests from it
    /// (browsers send <c>Origin</c> on same-origin POSTs; there is no preflight) are served normally.
    /// </summary>
    [Fact]
    public async Task The_admin_apps_own_origin_is_served()
    {
        var school = await TestSchools.CreateAsync(Api, "cors-okulu");
        await SeedUserAsync("admin.cors@example.com", UserRole.SchoolAdmin, school);
        var adminOrigin = Http.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var ct = TestContext.Current.CancellationToken;

        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "admin.cors@example.com", password = OwnPassword }),
        };
        login.Headers.Add("Origin", adminOrigin);
        var loginResponse = await Http.SendAsync(login, ct);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await SessionFromAsync(loginResponse);

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        me.Headers.Add("Origin", adminOrigin);
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        var meResponse = await Http.SendAsync(me, ct);
        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        CorsHeadersOf(loginResponse).ShouldBeEmpty("Same-origin needs no CORS headers.");
        CorsHeadersOf(meResponse).ShouldBeEmpty("Same-origin needs no CORS headers.");
    }

    private static HttpRequestMessage CrossOriginRequest(string method, string path, string origin)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", origin);
        if (method == "OPTIONS")
        {
            // A browser preflight for a credentialed JSON request.
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization,x-requested-with");
        }
        else if (method == "POST")
        {
            request.Content = JsonContent.Create(new { email = "kimse@example.com", password = "Yanlis-Sifre-123" });
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        }

        return request;
    }

    private static List<string> CorsHeadersOf(HttpResponseMessage response) =>
        [.. response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Key)];

    /// <summary>Puts CORS in front of the real pipeline, the way <c>app.UseCors()</c> would be added.</summary>
    private sealed class CorsFirstStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseCors();
            next(app);
        };
    }
}
