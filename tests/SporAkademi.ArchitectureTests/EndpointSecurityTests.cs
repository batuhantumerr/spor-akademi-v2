using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SporAkademi.ArchitectureTests;

/// <summary>NFR-SEC-07: every endpoint requires authentication unless it is on this reviewed list.</summary>
public sealed class EndpointSecurityTests : IDisposable
{
    private static readonly string[] AnonymousEndpoints =
    [
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json", // Development and Testing only
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
        "/api/v1/auth/logout",
        "/api/v1/auth/password-reset/request", // P2-F4: always 202, rate limited (DEC-353)
        "/api/v1/auth/password-reset/confirm", // P2-F4: needs a set-password token
    ];

    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none",
                ["Jwt:SigningKey"] = new string('a', 64),
                ["Serilog:MinimumLevel:Default"] = "Fatal",
            }));
        });

    [Fact]
    public void Only_reviewed_endpoints_allow_anonymous_access()
    {
        var anonymous = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .ToList();

        anonymous.ShouldBeSubsetOf(AnonymousEndpoints);
    }

    [Fact]
    public void Fallback_policy_requires_an_authenticated_user()
    {
        var options = _factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        options.FallbackPolicy.ShouldNotBeNull();
        options.FallbackPolicy.Requirements.ShouldContain(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void The_job_panel_requires_its_platform_admin_policy()
    {
        // DEC-332: the Hangfire Dashboard shows every school's jobs.
        var panel = Endpoints().Single(e => e.RoutePattern.RawText!.StartsWith("/jobs", StringComparison.Ordinal));

        panel.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
        panel.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ShouldContain("JobsDashboard");
    }

    public void Dispose() => _factory.Dispose();

    private List<RouteEndpoint> Endpoints() =>
        [.. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
}
