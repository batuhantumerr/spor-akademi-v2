using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Application;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure;
using SporAkademi.Infrastructure.Messaging;
using SporAkademi.Notifications.Observability;

namespace SporAkademi.Notifications;

/// <summary>
/// The application and infrastructure layers wired like the API (DEC-630), without HTTP authentication and endpoints: the
/// process consumes the notification (e-mail) messages from RabbitMQ and answers the health probes. What distinguishes it
/// from the API is set in code, after every configuration source, so a deployment's configuration cannot widen it:
/// only the <c>notifications</c> consumer group runs (the real-time handlers need the API's SignalR hub and would drop
/// their messages here), no Hangfire server (the API owns the recurring jobs) and no outbox polling (the API publishes).
/// </summary>
internal static class NotificationsHost
{
    /// <param name="settings">Extra configuration (tests); the fixed worker settings below still win.</param>
    /// <param name="configureServices">Runs after the layers are registered (tests replace the e-mail sender or add handlers).</param>
    public static WebApplication Create(
        string[] args,
        IDictionary<string, string?>? settings = null,
        string? environment = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = environment });
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        // Last source: wins over files, environment variables and the settings above (the consumer selection is fixed below).
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:ServerEnabled"] = "false",
            ["Outbox:Scheduler"] = "None",
        });

        // A worker without a broker would consume nothing and still look healthy: refuse to start instead.
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("RabbitMq")))
        {
            throw new InvalidOperationException("ConnectionStrings:RabbitMq is required: the notifications worker consumes from the broker.");
        }

        // Without an SMTP host the e-mail sender only logs the template name and reports success: the message would be
        // acknowledged and the e-mail lost. Only Development and Testing may run like that.
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")
            && string.IsNullOrWhiteSpace(builder.Configuration["Email:Smtp:Host"]))
        {
            throw new InvalidOperationException("Email:Smtp:Host is required outside Development: without it the worker would acknowledge e-mails it never sends.");
        }

        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

        builder.AddWorkerLogging();
        builder.AddWorkerTelemetry();
        builder.AddWorkerErrorTracking();
        // Registered before the layers, which only add fallbacks for these. No request, no signed-in user; access tokens are
        // issued by the API only (a handler of this process never signs anyone in).
        builder.Services.AddSingleton<IAccessTokenIssuer, NoAccessTokens>();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration, InfrastructureProfile.Worker);
        // Options are post-configured: a list setting (Modules, Groups) cannot be cleared from a configuration source.
        builder.Services.RestrictConsumersToGroup(OutboxConsumerGroups.Notifications);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        // Liveness: the process answers, no dependency checks (an outage must not restart a healthy process).
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        // Readiness: the database and the broker, with this process's consumers running. Stricter than the API: a worker whose
        // consumers do not run is not ready, so "rabbitmq" reporting Degraded (unreachable, or a consumer missing) is 503 here.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Name is "database" or "rabbitmq",
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });
        return app;
    }
}

/// <summary>The worker issues no access tokens; the API does.</summary>
internal sealed class NoAccessTokens : IAccessTokenIssuer
{
    public AccessToken Issue(User user, DateTimeOffset now) => throw new NotSupportedException("The notifications worker issues no access tokens.");
}
