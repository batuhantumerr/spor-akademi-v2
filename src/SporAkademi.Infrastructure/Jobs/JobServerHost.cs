using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
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
/// hosts can switch it) and registers the recurring jobs, retrying both while the database is unreachable. The process
/// running a server owns the recurring job definitions, so a host without one leaves the shared storage alone.
/// </summary>
internal sealed partial class JobServerHost(
    IServiceProvider services,
    JobServerIdentity identity,
    IOptions<JobsOptions> options,
    IOptions<OutboxOptions> outbox,
    ILogger<JobServerHost> logger) : BackgroundService
{
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

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

        // Starting the server and registering the jobs both need the storage; while the database is unreachable both
        // are retried, so the API stays up (DEC-330).
        BackgroundJobServer? server = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    server ??= new BackgroundJobServer(serverOptions, services.GetRequiredService<JobStorage>());
                    RecurringJobs.Register(services.GetRequiredService<IRecurringJobManager>(), settings, outbox.Value);
                    break;
                }
#pragma warning disable CA1031 // A database outage at startup must not stop the host; it is retried.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    LogStartFailed(logger, exception.GetType().FullName ?? exception.GetType().Name);
                }

                await Task.Delay(RetryDelay, stoppingToken);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: running jobs get their cancellation token and the server waits for them.
        }

        if (server is not null)
        {
            server.SendStop();
            await server.WaitForShutdownAsync(CancellationToken.None);
            server.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting the job server or registering recurring jobs failed with {Error}; retrying")]
    private static partial void LogStartFailed(ILogger logger, string error);
}

/// <summary>
/// Readiness (DEC-335): Degraded, never Unhealthy, when this process should run a job server but its heartbeat is
/// missing or stale; the API itself still serves requests. A process without a server reports Healthy. The data
/// holds counts only. The storage is read at most every <see cref="JobsMonitoringSnapshot.MaxAge"/>, so frequent
/// readiness probes do not each run Hangfire's statistics queries.
/// </summary>
internal sealed class JobsHealthCheck(JobsMonitoringSnapshot snapshot, HeartbeatClock clock, IOptions<JobsOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.ServerEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No job server runs in this process.", new Dictionary<string, object> { ["serverEnabled"] = false }));
        }

        var (statistics, heartbeat) = snapshot.Read();

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

/// <summary>The statistics and this process's last heartbeat, read from the storage at most every 10 seconds.</summary>
internal sealed class JobsMonitoringSnapshot(JobStorage storage, JobServerIdentity identity, HeartbeatClock clock)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private (StatisticsDto Statistics, DateTime? Heartbeat) _value;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public (StatisticsDto Statistics, DateTime? Heartbeat) Read()
    {
        lock (_gate)
        {
            var now = clock.Time.GetUtcNow();
            if (now - _readAt < MaxAge)
            {
                return _value;
            }

            IMonitoringApi monitoring = storage.GetMonitoringApi();
            var statistics = monitoring.GetStatistics();
            var heartbeat = monitoring.Servers()
                .Where(s => s.Name.StartsWith(identity.Name, StringComparison.Ordinal))
                .Select(s => s.Heartbeat)
                .Max();
            _value = (statistics, heartbeat);
            _readAt = now;
            return _value;
        }
    }
}
