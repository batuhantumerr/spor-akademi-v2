namespace SporAkademi.Infrastructure.Outbox;

/// <summary>Configuration section <c>Outbox</c> (DEC-300). TimeSpans use the <c>hh:mm:ss</c> format.</summary>
internal sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>
    /// What drives <see cref="OutboxProcessor"/> (DEC-333): Hangfire recurring jobs (default), the hosted polling
    /// worker, or nothing in this process (tests call the processor directly; another process may run it).
    /// </summary>
    public OutboxScheduler Scheduler { get; set; } = OutboxScheduler.Hangfire;

    /// <summary>Wait between polls when the last batch was not full.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 20;

    /// <summary>Deliveries before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Delay after the first failure; doubles per failure up to <see cref="MaxRetryDelay"/>, with jitter.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A handler running longer is cancelled and the attempt counts as failed.</summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Processed messages older than this are deleted. Dead-lettered messages are kept until handled.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Cleanup interval of the polling worker; the Hangfire job follows <c>Jobs:OutboxCleanupCron</c>.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The health check reports Degraded when a pending message has been due for longer than this.</summary>
    public TimeSpan PendingAgeWarning { get; set; } = TimeSpan.FromMinutes(15);

    internal bool IsValid() =>
        Enum.IsDefined(Scheduler)
        && PollingInterval > TimeSpan.Zero
        && BatchSize is > 0 and <= 1000
        && MaxAttempts is > 0 and <= 100
        && BaseRetryDelay > TimeSpan.Zero
        && MaxRetryDelay >= BaseRetryDelay
        && HandlerTimeout > TimeSpan.Zero
        && Retention > TimeSpan.Zero
        && CleanupInterval > TimeSpan.Zero
        && PendingAgeWarning > TimeSpan.Zero;
}

public enum OutboxScheduler
{
    /// <summary>Recurring jobs <c>outbox-process</c> and <c>outbox-cleanup</c> on the Hangfire server (P2-F2).</summary>
    Hangfire,

    /// <summary>The hosted <see cref="OutboxWorker"/> polling loop (P2-F3).</summary>
    Worker,

    /// <summary>Not processed by this process.</summary>
    None,
}
