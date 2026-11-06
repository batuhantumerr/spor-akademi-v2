namespace SporAkademi.Infrastructure.Outbox;

/// <summary>Configuration section <c>Outbox</c> (DEC-300). TimeSpans use the <c>hh:mm:ss</c> format.</summary>
internal sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>Runs the hosted polling worker. Off in tests, which call the processor directly, and when a scheduler (P2-F2) drives it.</summary>
    public bool Enabled { get; set; } = true;

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

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The health check reports Degraded when a pending message is older than this.</summary>
    public TimeSpan PendingAgeWarning { get; set; } = TimeSpan.FromMinutes(15);

    internal bool IsValid() =>
        PollingInterval > TimeSpan.Zero
        && BatchSize is > 0 and <= 1000
        && MaxAttempts is > 0 and <= 100
        && BaseRetryDelay > TimeSpan.Zero
        && MaxRetryDelay >= BaseRetryDelay
        && HandlerTimeout > TimeSpan.Zero
        && Retention > TimeSpan.Zero
        && CleanupInterval > TimeSpan.Zero
        && PendingAgeWarning > TimeSpan.Zero;
}
