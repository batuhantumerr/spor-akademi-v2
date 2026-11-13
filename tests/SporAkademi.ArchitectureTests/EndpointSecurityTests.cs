using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
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
        // P2-F1 (DEC-344): signed or re-queried provider endpoints; rate limited, school from the stored payment.
        "/api/v1/payment-webhooks",
        "/api/v1/payment-callbacks",
        "/api/v1/sandbox-checkout/complete",
        "/api/v1/auth/password-reset/request", // P2-F4: always 202, rate limited (DEC-353)
        "/api/v1/auth/password-reset/confirm", // P2-F4: needs a set-password token
    ];

    /// <summary>
    /// P4-03 (A01, ASVS 4.1.3): signed-in endpoints that deliberately accept every role. Everything else names its roles,
    /// so a new endpoint added without a role gate (which the fallback policy alone would leave open to a Parent token)
    /// fails here.
    /// </summary>
    private static readonly string[] AnyRoleEndpoints =
    [
        "GET /api/v1/me", // the signed-in user's own profile
        "POST /api/v1/auth/change-password", // the signed-in user's own password (also while it must be changed)
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
    [Trait("Story", "P4-03")]
    public void Every_signed_in_endpoint_names_the_roles_that_may_call_it()
    {
        var open = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => !NamesRoles(e))
            .Select(e => string.Join(' ', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []) + " /" + e.RoutePattern.RawText!.TrimStart('/'))
            .ToList();

        open.ShouldBe(AnyRoleEndpoints, ignoreOrder: true);
    }

    private static bool NamesRoles(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>().Any(p => p.Requirements.OfType<RolesAuthorizationRequirement>().Any())
        || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Roles) || (a.Policy is { } policy && NamedPolicies.Contains(policy)));

    /// <summary>Named policies reviewed to gate by role or stricter (the job panel: active Platform Admin with its own cookie).</summary>
    private static readonly string[] NamedPolicies = ["JobsDashboard"];

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
