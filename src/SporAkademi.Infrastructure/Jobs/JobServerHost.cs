using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SporAkademi.Infrastructure.Outbox;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>Names this process's Hangfire server, so readiness can find its own heartbeat among other instances'.</summary>
internal sealed class JobServerIdentity
{
    public string Name { get; } = $"spor-akademi-api-{Guid.CreateVersion7():N}";
}

/// <summary>
/// Runs the Hangfire server inside the API process when <c>Jobs:ServerEnabled</c> is true (read at start, so test
/// hosts can switch it), then registers the recurring jobs, retrying while the database is unreachable. The process
/// running a server owns the recurring job definitions, so a host without one leaves the shared storage alone.
/// </summary>
internal sealed partial class JobServerHost(
    IServiceProvider services,
    JobServerIdentity identity,
    IOptions<JobsOptions> options,
    IOptions<OutboxOptions> outbox,
    ILogger<JobServerHost> logger) : BackgroundService
{
    internal static readonly TimeSpan RegistrationRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ServerEnabled)
        {
            return;
        }

        var serverOptions = new BackgroundJobServerOptions
        {
            ServerName = identity.Name,
            WorkerCount = settings.WorkerCount,
            SchedulePollingInterval = settings.SchedulePollingInterval,
            Activator = services.GetRequiredService<JobActivator>(),
            FilterProvider = services.GetService<IJobFilterProvider>() ?? JobFilterProviders.Providers,
            TimeZoneResolver = services.GetService<ITimeZoneResolver>(),
        };

        using var server = new BackgroundJobServer(serverOptions, services.GetRequiredService<JobStorage>());
        await RegisterRecurringJobsAsync(settings, stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: running jobs get their cancellation token and the server waits for them.
        }

        server.SendStop();
        await server.WaitForShutdownAsync(CancellationToken.None);
    }

    private async Task RegisterRecurringJobsAsync(JobsOptions settings, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RecurringJobs.Register(services.GetRequiredService<IRecurringJobManager>(), settings, outbox.Value);
                return;
            }
#pragma warning disable CA1031 // A database outage at startup must not stop the host; registration is retried.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRegistrationFailed(logger, exception.GetType().FullName ?? exception.GetType().Name);
            }

            try
            {
                await Task.Delay(RegistrationRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering recurring jobs failed with {Error}; retrying")]
    private static partial void LogRegistrationFailed(ILogger logger, string error);
}

/// <summary>
/// Readiness (DEC-335): Degraded, never Unhealthy, when this process should run a job server but its heartbeat is
/// missing or stale; the API itself still serves requests. A process without a server reports Healthy. The data
/// holds counts only.
/// </summary>
internal sealed class JobsHealthCheck(JobStorage storage, JobServerIdentity identity, HeartbeatClock clock, IOptions<JobsOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.ServerEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No job server runs in this process.", new Dictionary<string, object> { ["serverEnabled"] = false }));
        }

        IMonitoringApi monitoring = storage.GetMonitoringApi();
        var statistics = monitoring.GetStatistics();
        var heartbeat = monitoring.Servers()
            .Where(s => s.Name.StartsWith(identity.Name, StringComparison.Ordinal))
            .Select(s => s.Heartbeat)
            .Max();

        var data = new Dictionary<string, object>
        {
            ["serverEnabled"] = true,
            ["servers"] = statistics.Servers,
            ["enqueued"] = statistics.Enqueued,
            ["failed"] = statistics.Failed,
        };

        var now = clock.Time.GetUtcNow().UtcDateTime;
        return Task.FromResult(heartbeat is { } beat && now - DateTime.SpecifyKind(beat, DateTimeKind.Utc) <= settings.HeartbeatWarning
            ? HealthCheckResult.Healthy(data: data)
            : HealthCheckResult.Degraded("The job server of this process has no recent heartbeat.", data: data));
    }
}

/// <summary>
/// Hangfire stamps heartbeats with the system clock, not the application's <see cref="TimeProvider"/> (which tests
/// move), so readiness compares them with the system clock; it is registered in the composition root.
/// </summary>
internal sealed record HeartbeatClock(TimeProvider Time);
