namespace SporAkademi.Infrastructure.Jobs;

/// <summary>Configuration section <c>Jobs</c> (P2-F2, DEC-330). TimeSpans use the <c>hh:mm:ss</c> format; cron expressions are UTC.</summary>
public sealed class JobsOptions
{
    public const string Section = "Jobs";

    /// <summary>Runs a Hangfire server (workers and schedulers) in the API process. Off in tests that do not exercise it.</summary>
    public bool ServerEnabled { get; set; } = true;

    public int WorkerCount { get; set; } = 2;

    /// <summary>How often an idle worker looks for an enqueued job.</summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the schedulers look for due recurring and delayed jobs.</summary>
    public TimeSpan SchedulePollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Outbox delivery (with seconds: six fields).</summary>
    public string OutboxCron { get; set; } = "*/10 * * * * *";

    public string OutboxCleanupCron { get; set; } = "15 * * * *";

    public string RefreshTokenCleanupCron { get; set; } = "30 3 * * *";

    /// <summary>Fee reminders (P2-F4, DEC-355): 06:00 UTC, 09:00 in Istanbul.</summary>
    public string DuesReminderCron { get; set; } = "0 6 * * *";

    /// <summary>One outbox run delivers batches until one is not full, at most this many.</summary>
    public int MaxOutboxBatchesPerRun { get; set; } = 50;

    /// <summary>Readiness reports Degraded when this process's server has not sent a heartbeat for this long.</summary>
    public TimeSpan HeartbeatWarning { get; set; } = TimeSpan.FromMinutes(2);

    public JobsDashboardOptions Dashboard { get; set; } = new();

    internal bool IsValid() =>
        WorkerCount is > 0 and <= 50
        && QueuePollInterval > TimeSpan.Zero
        && SchedulePollingInterval > TimeSpan.Zero
        && IsCron(OutboxCron)
        && IsCron(OutboxCleanupCron)
        && IsCron(RefreshTokenCleanupCron)
        && IsCron(DuesReminderCron)
        && MaxOutboxBatchesPerRun is > 0 and <= 10_000
        && HeartbeatWarning > TimeSpan.Zero
        && Dashboard.SessionLifetime > TimeSpan.Zero
        && Dashboard.SessionLifetime <= TimeSpan.FromHours(1);

    /// <summary>Hangfire parses the expressions when the jobs are registered; here they only must be present.</summary>
    private static bool IsCron(string expression) => !string.IsNullOrWhiteSpace(expression);
}

/// <summary>The job panel (<c>/jobs</c>, DEC-332).</summary>
public sealed class JobsDashboardOptions
{
    /// <summary>The panel shows jobs but offers no actions (retry, delete, trigger). On by default; turn off to operate jobs.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Absolute lifetime of a panel session; a new session needs a new call from the admin app.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The panel's "back to site" link.</summary>
    public string AppPath { get; set; } = "/";
}
