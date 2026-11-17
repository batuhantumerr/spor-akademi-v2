using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace SporAkademi.Infrastructure.Messaging;

/// <summary>A message as the outbox hands it to the broker: the stored row, not a deserialized object.</summary>
internal sealed record OutboxEnvelope(Guid MessageId, string Type, string Payload, Guid? TenantId, DateTimeOffset OccurredAt);

/// <summary>The broker did not take the message (down, nack, no confirm in time): nothing was lost, the outbox keeps it.</summary>
internal sealed class BrokerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The broker accepted the message but no queue is bound for its type: no consumer exists for it.</summary>
internal sealed class NoRouteException() : Exception("No queue is bound for the message type.");

/// <summary>Where <see cref="Outbox.OutboxProcessor"/> publishes instead of calling handlers in process.</summary>
internal interface IOutboxPublisher
{
    bool IsConfigured { get; }

    /// <summary>Returns after the broker confirmed the message is persisted on its queues.</summary>
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// The RabbitMQ connection of the process (P5-03, DEC-620): one connection with automatic recovery, one publishing channel
/// with publisher confirms, and the topology (a durable topic exchange, one durable quorum queue per consumer bound by the
/// message type, a retry queue per consumer and one dead-letter queue). Declaring is idempotent and done by publishers and
/// consumers alike. Connected lazily, so a host starts (and the outbox keeps its messages) while the broker is down.
/// </summary>
internal sealed partial class RabbitMqBroker(
    IOptions<RabbitMqOptions> options,
    ConsumerCatalog catalog,
    ILogger<RabbitMqBroker> logger) : IOutboxPublisher, IAsyncDisposable
{
    /// <summary>Requeues of one delivery (settling failed) before the broker dead-letters it.</summary>
    public const int SettleFailureLimit = 5;

    public const string TenantHeader = "tenant-id";
    public const string AttemptHeader = "x-attempt";
    public const string ErrorHeader = "x-error";
    public const string OriginalQueueHeader = "x-original-queue";
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publishChannel;

    // Real time: since when the connection is known to be closed (0 = not).
    private const long ReconnectAfterMilliseconds = 60_000;
    private long _closedSince;

    private RabbitMqOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;

    public bool IsConnectionOpen => _connection is { IsOpen: true };

    public string DeadLetterExchange => Options.Exchange + ".dlx";

    public string DeadLetterQueue => Options.Exchange + ".dead-letter";

    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var headers = new Dictionary<string, object?>
        {
            [AttemptHeader] = 1,
        };
        if (envelope.TenantId is { } tenant)
        {
            headers[TenantHeader] = tenant.ToString();
        }

        // W3C trace context: the consumer's span continues the trace of this publish.
        if (Activity.Current is { } current && current.IdFormat == ActivityIdFormat.W3C)
        {
            headers[TraceParentHeader] = current.Id;
            if (!string.IsNullOrEmpty(current.TraceStateString))
            {
                headers[TraceStateHeader] = current.TraceStateString;
            }
        }

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = envelope.MessageId.ToString(),
            Type = envelope.Type,
            ContentType = "application/json",
            Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds()),
            Headers = headers,
        };

        // Mandatory: a type without any bound queue is returned instead of silently dropped.
        await PublishRawAsync(Options.Exchange, envelope.Type, mandatory: true, properties, Encoding.UTF8.GetBytes(envelope.Payload), cancellationToken);
    }

    /// <summary>Publishes with confirm. Also used by consumers for the retry and dead-letter copies.</summary>
    public async Task PublishRawAsync(
        string exchange,
        string routingKey,
        bool mandatory,
        BasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        try
        {
            var channel = await GetPublishChannelAsync(cancellationToken);
            // A network wait: real time, not the business clock.
            using var timeout = new CancellationTokenSource(Options.PublishTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await channel.BasicPublishAsync(exchange, routingKey, mandatory, properties, body, linked.Token);
        }
        catch (PublishException exception) when (exception.IsReturn)
        {
            throw new NoRouteException();
        }
        catch (Exception exception) when (exception is not (BrokerUnavailableException or NoRouteException)
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Only the type of the failure is kept: a broker error text can echo the connection details.
            throw new BrokerUnavailableException(exception.GetType().Name, exception);
        }
    }

    /// <summary>Connects (or waits for the client's automatic recovery) and returns the open connection.</summary>
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: false } && _closedSince != 0 && Environment.TickCount64 - _closedSince > ReconnectAfterMilliseconds)
            {
                // The client's recovery gave up or the server closed it for good: start over with a new connection.
                _connection.Dispose();
                _connection = null;
                _publishChannel?.Dispose();
                _publishChannel = null;
                _closedSince = 0;
            }

            if (_connection is null)
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(Options.ConnectionString ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq is not set.")),
                    ClientProvidedName = "spor-akademi",
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                    RequestedConnectionTimeout = Options.ConnectTimeout,
                };
                try
                {
                    _connection = await factory.CreateConnectionAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new BrokerUnavailableException(exception.GetType().Name, exception);
                }
            }

            if (_connection.IsOpen)
            {
                _closedSince = 0;
            }
            else
            {
                if (_closedSince == 0)
                {
                    _closedSince = Environment.TickCount64;
                }

                // Connection lost: the client keeps reconnecting by itself; this call just reports the outage.
                throw new BrokerUnavailableException("ConnectionClosed");
            }

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a channel for a consumer and makes sure the topology exists.</summary>
    public async Task<IChannel> CreateConsumerChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await DeclareTopologyAsync(channel, cancellationToken);
        return channel;
    }

    /// <summary>True when a connection is (or can be made) open; the health check uses it.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetConnectionAsync(cancellationToken);
            return true;
        }
        catch (BrokerUnavailableException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_publishChannel is not null)
            {
                await _publishChannel.CloseAsync();
            }

            if (_connection is not null)
            {
                await _connection.CloseAsync();
            }
        }
#pragma warning disable CA1031 // Shutting down: a broken connection is not worth failing the host for.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogCloseFailed(logger, exception.GetType().Name);
        }

        _publishChannel?.Dispose();
        _connection?.Dispose();
        _gate.Dispose();
    }

    private async Task<IChannel> GetPublishChannelAsync(CancellationToken cancellationToken)
    {
        if (_publishChannel is { IsOpen: true } existing)
        {
            return existing;
        }

        var connection = await GetConnectionAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_publishChannel is { IsOpen: true } created)
            {
                return created;
            }

            _publishChannel?.Dispose();
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await DeclareTopologyAsync(channel, cancellationToken);
            _publishChannel = channel;
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var quorum = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };

        // Bounds a delivery that keeps being requeued because it cannot be settled (e.g. the retry copy cannot be published):
        // after the limit the broker itself moves it to the dead-letter queue instead of looping forever.
        var consumed = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = DeadLetterExchange,
            ["x-delivery-limit"] = SettleFailureLimit,
        };

        await channel.ExchangeDeclareAsync(Options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, arguments: quorum, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, string.Empty, cancellationToken: cancellationToken);

        foreach (var consumer in catalog.All)
        {
            await channel.QueueDeclareAsync(consumer.Queue, durable: true, exclusive: false, autoDelete: false, arguments: consumed, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(consumer.Queue, Options.Exchange, consumer.Type.Name, cancellationToken: cancellationToken);

            // A failed message waits here (its own expiration is the retry delay) and then returns to the consumer's queue
            // through the default exchange; the delay is per message, so changing it never invalidates the queue.
            var retry = new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = consumer.Queue,
            };
            await channel.QueueDeclareAsync(consumer.RetryQueue, durable: true, exclusive: false, autoDelete: false, arguments: retry, cancellationToken: cancellationToken);
        }

        LogTopologyDeclared(logger, catalog.All.Count);
    }

    /// <summary>A header read from a delivery: the broker returns strings as bytes.</summary>
    public static string? HeaderString(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            }
            : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ topology declared for {Consumers} consumers")]
    private static partial void LogTopologyDeclared(ILogger logger, int consumers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing the RabbitMQ connection failed with {Error}")]
    private static partial void LogCloseFailed(ILogger logger, string error);
}
