using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace SporAkademi.Api;

internal static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process answers. No dependency checks, so a database outage
        // does not make the orchestrator restart a healthy process.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        // Readiness: the database is reachable.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) })
            .AllowAnonymous();

        return app;
    }
}
