using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Messaging;

/// <summary>
/// Runs the consumers of this process (P5-03, DEC-621 – DEC-625): one channel and one queue per
/// <see cref="IOutboxMessageHandler{TMessage}"/>, for the modules <see cref="ConsumerSelection"/> selects. A delivery runs
/// the handler in its own DI scope under the message's school (exactly like the in-process path), and in one transaction
/// with the consumer's inbox row, so a redelivered message is skipped. A failure goes to the consumer's retry queue with
/// the attempt counted in a header; after <see cref="RabbitMqOptions.MaxDeliveryAttempts"/> it goes to the dead-letter
/// queue. The original delivery is acknowledged only after that copy is confirmed, so nothing is lost on the way.
/// </summary>
internal sealed partial class RabbitMqConsumerService(
    RabbitMqBroker broker,
    ConsumerCatalog catalog,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider clock,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    // Pacing of reconnects and redeliveries is real time, whatever the (testable) business clock says.
    private static readonly TimeSpan ConnectRetry = TimeSpan.FromSeconds(5);
    private readonly List<IChannel> _channels = [];

    private RabbitMqOptions Options => options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.IsConfigured || !Options.Consumers.Enabled)
        {
            return;
        }

        var selected = catalog.All.Where(c => Options.Consumers.Includes(c.Module)).ToList();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var registration in selected)
                {
                    await StartConsumerAsync(registration, stoppingToken);
                }

                LogStarted(logger, selected.Count);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The broker may be down at start; consumers start when it is back.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogStartFailed(logger, exception.GetType().Name);
                await CloseChannelsAsync();
                try
                {
                    await Task.Delay(ConnectRetry, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await CloseChannelsAsync();
    }

    private async Task StartConsumerAsync(ConsumerRegistration registration, CancellationToken stoppingToken)
    {
        var channel = await broker.CreateConsumerChannelAsync(stoppingToken);
        lock (_channels)
        {
            _channels.Add(channel);
        }

        await channel.BasicQosAsync(0, Options.PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(registration, channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(registration.Queue, autoAck: false, consumer, stoppingToken);
    }

    private async Task CloseChannelsAsync()
    {
        IChannel[] channels;
        lock (_channels)
        {
            channels = [.. _channels];
            _channels.Clear();
        }

        foreach (var channel in channels)
        {
            try
            {
                await channel.CloseAsync();
            }
#pragma warning disable CA1031 // Shutting down or restarting: a broken channel is simply dropped.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Dropped.
            }

            channel.Dispose();
        }
    }

    private async Task OnDeliveryAsync(ConsumerRegistration registration, IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessAsync(registration, delivery, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: the broker redelivers it to the next consumer; the attempt does not count.
            await NackAsync(channel, delivery);
        }
#pragma warning disable CA1031 // Whatever went wrong (e.g. the broker refused the retry copy), the delivery stays on its queue.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogDeliveryFailed(logger, registration.Queue, exception.GetType().Name);
            await NackAsync(channel, delivery);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }
    }

    private static async Task NackAsync(IChannel channel, BasicDeliverEventArgs delivery)
    {
        try
        {
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
#pragma warning disable CA1031 // The channel is gone: the broker requeues unacknowledged deliveries itself.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Requeued by the broker.
        }
    }

    /// <summary>Handles one delivery; returns when it may be acknowledged (done, duplicate, or moved to retry / dead letter).</summary>
    private async Task ProcessAsync(ConsumerRegistration registration, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var properties = delivery.BasicProperties;
        var headers = properties.Headers;
        var attempt = int.TryParse(RabbitMqBroker.HeaderString(headers, RabbitMqBroker.AttemptHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
        var tenantId = Guid.TryParse(RabbitMqBroker.HeaderString(headers, RabbitMqBroker.TenantHeader), out var tenant) ? tenant : (Guid?)null;
        var hasId = Guid.TryParse(properties.MessageId, out var messageId);
        var occurredAt = properties.Timestamp.UnixTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime) : clock.GetUtcNow();

        using var activity = StartConsumerActivity(headers, registration);
        activity?.SetTag("spor_akademi.messaging.message_type", registration.Type.Name);
        activity?.SetTag("spor_akademi.messaging.attempt", attempt);

        object payload;
        try
        {
            payload = registration.Type.Deserialize(Encoding.UTF8.GetString(delivery.Body.Span));
        }
        catch (JsonException)
        {
            // Retrying cannot fix a payload that does not match the contract.
            await DeadLetterAsync(registration, delivery, attempt, OutboxProcessor.InvalidPayloadError, stoppingToken);
            return;
        }

        if (!hasId)
        {
            await DeadLetterAsync(registration, delivery, attempt, "InvalidMessageId", stoppingToken);
            return;
        }

        var context = new OutboxMessageContext(messageId, tenantId, occurredAt, attempt);
        try
        {
            var handled = await HandleOnceAsync(registration, payload, context, stoppingToken);
            AppTelemetry.MessagingMessages.Add(1, new("operation", handled ? "consumed" : "duplicate"), new("message_type", registration.Type.Name));
            LogConsumed(logger, messageId, registration.Queue, attempt, handled);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any handler failure is retried; the consumer must survive it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The error is an exception type, never its message (it may hold personal data).
            var error = exception is OperationCanceledException ? OutboxProcessor.TimeoutError : exception.GetType().FullName ?? exception.GetType().Name;
            activity?.SetStatus(ActivityStatusCode.Error, error);
            if (attempt >= Options.MaxDeliveryAttempts)
            {
                await DeadLetterAsync(registration, delivery, attempt, error, stoppingToken);
            }
            else
            {
                await RetryAsync(registration, delivery, attempt, error, stoppingToken);
            }
        }
    }

    /// <returns><c>true</c> when the handler ran, <c>false</c> when this consumer had already handled the message.</returns>
    private async Task<bool> HandleOnceAsync(ConsumerRegistration registration, object payload, OutboxMessageContext context, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(outboxOptions.Value.HandlerTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        await using var scope = scopeFactory.CreateAsyncScope();
        // The school of the message, exactly like the in-process path (tenant filters, write guard and RLS apply).
        scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(context.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(linked.Token);

        // The inbox row and the handler's own changes commit together; a concurrent duplicate waits here for the first
        // delivery's outcome (unique key), then sees the row and skips.
        var inserted = await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO outbox.inbox (consumer, message_id, tenant_id, processed_at) VALUES (@consumer, @id, @tenant, @now) ON CONFLICT DO NOTHING",
            [
                new NpgsqlParameter("consumer", registration.Queue),
                new NpgsqlParameter("id", context.MessageId),
                new NpgsqlParameter("tenant", NpgsqlDbType.Uuid) { Value = (object?)context.TenantId ?? DBNull.Value },
                new NpgsqlParameter("now", clock.GetUtcNow()),
            ],
            linked.Token);
        if (inserted == 0)
        {
            return false;
        }

        await registration.Type.InvokeHandlerAsync(scope.ServiceProvider, registration.HandlerType, payload, context, linked.Token);
        await db.SaveChangesAsync(linked.Token);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private async Task RetryAsync(ConsumerRegistration registration, BasicDeliverEventArgs delivery, int attempt, string error, CancellationToken cancellationToken)
    {
        var properties = CopyProperties(delivery.BasicProperties, attempt + 1, error);
        properties.Expiration = ((long)Options.RetryDelay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        // The default exchange routes by queue name: the copy waits in the retry queue, then returns to the consumer's queue.
        await broker.PublishRawAsync(string.Empty, registration.RetryQueue, mandatory: true, properties, delivery.Body, cancellationToken);
        AppTelemetry.MessagingMessages.Add(1, new("operation", "failed"), new("message_type", registration.Type.Name));
        Activity.Current?.SetStatus(ActivityStatusCode.Error, error);
        LogRetryScheduled(logger, delivery.BasicProperties.MessageId, registration.Queue, attempt, error);
    }

    private async Task DeadLetterAsync(ConsumerRegistration registration, BasicDeliverEventArgs delivery, int attempt, string error, CancellationToken cancellationToken)
    {
        var properties = CopyProperties(delivery.BasicProperties, attempt, error);
        properties.Headers![RabbitMqBroker.OriginalQueueHeader] = registration.Queue;
        await broker.PublishRawAsync(broker.DeadLetterExchange, string.Empty, mandatory: true, properties, delivery.Body, cancellationToken);
        AppTelemetry.MessagingMessages.Add(1, new("operation", "failed"), new("message_type", registration.Type.Name));
        AppTelemetry.MessagingMessages.Add(1, new("operation", "dead_lettered"), new("message_type", registration.Type.Name));
        Activity.Current?.SetStatus(ActivityStatusCode.Error, error);
        LogDeadLettered(logger, delivery.BasicProperties.MessageId, registration.Queue, attempt, error);
    }

    private static BasicProperties CopyProperties(IReadOnlyBasicProperties source, int attempt, string error)
    {
        var headers = new Dictionary<string, object?>();
        if (source.Headers is not null)
        {
            foreach (var (key, value) in source.Headers)
            {
                headers[key] = value;
            }
        }

        headers[RabbitMqBroker.AttemptHeader] = attempt;
        headers[RabbitMqBroker.ErrorHeader] = error.Length <= 500 ? error : error[..500];
        return new BasicProperties
        {
            Persistent = true,
            MessageId = source.MessageId,
            Type = source.Type,
            ContentType = source.ContentType,
            Timestamp = source.Timestamp,
            Headers = headers,
        };
    }

    /// <summary>A consumer span in the trace of the publish (W3C <c>traceparent</c> header), when anything listens.</summary>
    private static Activity? StartConsumerActivity(IDictionary<string, object?>? headers, ConsumerRegistration registration)
    {
        var traceParent = RabbitMqBroker.HeaderString(headers, RabbitMqBroker.TraceParentHeader);
        var traceState = RabbitMqBroker.HeaderString(headers, RabbitMqBroker.TraceStateHeader);
        return traceParent is not null && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent)
            ? AppTelemetry.Source.StartActivity("messaging.consume", ActivityKind.Consumer, parent)
            : AppTelemetry.Source.StartActivity("messaging.consume", ActivityKind.Consumer, parentContext: default);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ consumers started: {Count}")]
    private static partial void LogStarted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting the RabbitMQ consumers failed with {Error}; retrying")]
    private static partial void LogStartFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A delivery on {Queue} could not be settled ({Error}); it stays on its queue")]
    private static partial void LogDeliveryFailed(ILogger logger, string queue, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Message {MessageId} consumed by {Queue} on attempt {Attempt} (handler ran: {Handled})")]
    private static partial void LogConsumed(ILogger logger, Guid messageId, string queue, int attempt, bool handled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} failed on {Queue} at attempt {Attempt} with {Error}; retry scheduled")]
    private static partial void LogRetryScheduled(ILogger logger, string? messageId, string queue, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} dead-lettered from {Queue} after {Attempt} attempts; last error {Error}")]
    private static partial void LogDeadLettered(ILogger logger, string? messageId, string queue, int attempt, string error);
}
