using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>DEC-054: the admin app and the API share one origin, so the API sends no CORS headers to anyone.</summary>
public sealed class CorsTests(DatabaseFixture database) : IntegrationTest(database)
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
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", ForeignOrigin);
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

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.NoContent, "A 204 to a preflight would hint at CORS handling.");
        var corsHeaders = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Key)
            .ToList();
        corsHeaders.ShouldBeEmpty();
    }
}
