namespace SporAkademi.Infrastructure.Messaging;

/// <summary>
/// Configuration section <c>RabbitMq</c> (P5-03, DEC-620 – DEC-629); the broker's address is
/// <c>ConnectionStrings:RabbitMq</c> (an <c>amqp://</c> or <c>amqps://</c> URI). Without it nothing here is active and the
/// outbox delivers in process as before. TimeSpans use the <c>hh:mm:ss</c> format.
/// </summary>
internal sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";
    public const string ConnectionStringName = "RabbitMq";

    /// <summary>Filled from <c>ConnectionStrings:RabbitMq</c>; never logged (it holds the password).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The durable topic exchange; the routing key is the message type (<c>module.event.vN</c>).</summary>
    public string Exchange { get; set; } = "spor-akademi.events";

    /// <summary>Deliveries of one consumer before the message goes to the dead-letter queue.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>How long a failed message waits in the consumer's retry queue before it is delivered again.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Unacknowledged messages per consumer.</summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>Waiting for the broker's publisher confirm; a longer wait counts as a broker outage.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The inbox (consumed message ids) is cleaned after this; it must outlast the longest redelivery.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Which consumers this process runs (P5-04 moves the notification consumers to a worker).</summary>
    public ConsumerSelection Consumers { get; set; } = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    internal bool IsValid() =>
        !string.IsNullOrWhiteSpace(Exchange)
        && MaxDeliveryAttempts is > 0 and <= 100
        && RetryDelay > TimeSpan.Zero && RetryDelay <= TimeSpan.FromHours(6)
        && PrefetchCount > 0
        && PublishTimeout > TimeSpan.Zero
        && ConnectTimeout > TimeSpan.Zero
        && InboxRetention > TimeSpan.Zero
        && (!IsConfigured || (Uri.TryCreate(ConnectionString, UriKind.Absolute, out var uri) && uri.Scheme is "amqp" or "amqps"));
}

internal sealed class ConsumerSelection
{
    /// <summary>Set to <c>false</c> on a host that only publishes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The modules (first segment of the message type, e.g. <c>billing</c>) whose consumers run here; empty means all.
    /// </summary>
    public string[] Modules { get; set; } = [];

    public bool Includes(string module) =>
        Modules.Length == 0 || Modules.Contains(module, StringComparer.OrdinalIgnoreCase);
}
