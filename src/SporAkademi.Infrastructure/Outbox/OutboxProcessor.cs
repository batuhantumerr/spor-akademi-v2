using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// Delivers due outbox messages (DEC-302). One call claims a batch with <c>FOR UPDATE SKIP LOCKED</c> in a
/// transaction, so concurrent processors (API instances, the polling worker, a P2-F2 job) never take the same
/// message; each message is handled in its own DI scope under its school, then marked processed, rescheduled with
/// backoff or dead-lettered, and the transaction commits. A crash before commit releases the claim and the batch is
/// delivered again: delivery is at least once.
/// </summary>
internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    OutboxMessageTypes types,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    public const string UnknownTypeError = "UnknownMessageType";
    public const string InvalidPayloadError = "InvalidPayload";
    public const string TimeoutError = "HandlerTimeout";
    public const string NoHandlerError = "NoHandler";

    private OutboxOptions Options => options.Value;

    /// <summary>Delivers at most one batch of due messages.</summary>
    /// <returns>How many messages were claimed (delivered, rescheduled or dead-lettered).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var batch = await db.Set<OutboxMessage>()
            .FromSql($"""
                SELECT * FROM outbox.messages
                WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT {Options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Shutting down: keep what is done; the rest stays pending and its claim ends with the transaction.
                break;
            }

            await DeliverAsync(message, cancellationToken);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return batch.Count;
    }

    /// <summary>Deletes processed messages older than the retention period; dead-lettered ones are kept.</summary>
    /// <returns>The number of deleted messages.</returns>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = clock.GetUtcNow() - Options.Retention;
        var deleted = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            LogCleanedUp(logger, deleted);
        }

        return deleted;
    }

    /// <summary>
    /// One span per delivery (P4-07, DEC-551): the handler's database and HTTP calls are its children, so a slow or failing
    /// delivery can be followed end to end; its attributes are the message type and the attempt, never the payload.
    /// </summary>
    private async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var activity = AppTelemetry.StartActivity("outbox.deliver");
        activity?.SetTag("spor_akademi.outbox.message_type", MessageTypeLabel(message));
        activity?.SetTag("spor_akademi.outbox.attempt", message.Attempts + 1);

        if (!types.TryGet(message.Type, out var type))
        {
            // E.g. written by a newer version during a rolling deploy: retried, then dead-lettered.
            Fail(message, UnknownTypeError);
            return;
        }

        object payload;
        try
        {
            payload = type.Deserialize(message.Payload);
        }
        catch (JsonException)
        {
            Fail(message, InvalidPayloadError);
            return;
        }

        var context = new OutboxMessageContext(message.Id, message.TenantId, message.OccurredAt, message.Attempts + 1);
        using var timeout = new CancellationTokenSource(Options.HandlerTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<int>? handling = null;
        try
        {
            await using var handlerScope = scopeFactory.CreateAsyncScope();
            handlerScope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(message.TenantId);

            // Cooperative handlers stop through the token; the wait itself is bounded too, so a handler that ignores
            // the token is abandoned at the timeout (or shutdown) instead of stalling the batch. An abandoned task
            // may still finish its side effect later; the message is retried anyway (at least once, DEC-302).
            handling = type.InvokeAsync(handlerScope.ServiceProvider, payload, context, linked.Token);
            var handlers = await handling.WaitAsync(Options.HandlerTimeout, clock, cancellationToken);
            if (handlers == 0)
            {
                // Nothing would ever deliver it: a registration is missing (e.g. a handler removed while messages were pending).
                Fail(message, NoHandlerError);
                return;
            }

            message.MarkProcessed(clock.GetUtcNow());
            AppTelemetry.OutboxMessages.Add(1, new("result", "delivered"), new("message_type", type.Name));
            LogDelivered(logger, message.Id, message.Type, context.Attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not the handler's fault: the attempt does not count.
            ObserveAbandoned(handling);
        }
        // The timeout token and the bounded wait expire at the same instant: a cooperative handler may already have
        // completed (cancelled) when the wait reports its TimeoutException, which is still the handler timeout.
        catch (Exception exception) when ((exception is OperationCanceledException && timeout.IsCancellationRequested)
            || (exception is TimeoutException && (handling?.IsCompleted == false || timeout.IsCancellationRequested)))
        {
            ObserveAbandoned(handling);
            Fail(message, TimeoutError);
        }
#pragma warning disable CA1031 // Any handler failure is retried; the processor must survive it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Fail(message, exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    /// <summary>A later failure of an abandoned handler is observed here, so it is not reported as unobserved.</summary>
    private static void ObserveAbandoned(Task? handling)
    {
        if (handling is { IsCompleted: false })
        {
            _ = handling.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <summary>A registered type's name, otherwise <c>unknown</c>: a stored type name is not trusted as a metric label.</summary>
    private string MessageTypeLabel(OutboxMessage message) => types.TryGet(message.Type, out var type) ? type.Name : "unknown";

    private void Fail(OutboxMessage message, string error)
    {
        var now = clock.GetUtcNow();
        var delay = OutboxRetryPolicy.Delay(message.Attempts + 1, Options.BaseRetryDelay, Options.MaxRetryDelay, Random.Shared.NextDouble());
        message.RecordFailure(error, now, Options.MaxAttempts, delay);
        var result = message.DeadLetteredAt is null ? "retried" : "dead_lettered";
        AppTelemetry.OutboxMessages.Add(1, new("result", result), new("message_type", MessageTypeLabel(message)));
        // The error is a code or an exception type, never an exception message.
        Activity.Current?.SetStatus(ActivityStatusCode.Error, error);

        if (message.DeadLetteredAt is not null)
        {
            LogDeadLettered(logger, message.Id, message.Type, message.Attempts, error);
        }
        else
        {
            LogRetryScheduled(logger, message.Id, message.Type, message.Attempts, error, message.NextAttemptAt);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} ({MessageType}) delivered on attempt {Attempt}")]
    private static partial void LogDelivered(ILogger logger, Guid messageId, string messageType, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({MessageType}) failed attempt {Attempt} with {Error}; next attempt at {NextAttemptAt}")]
    private static partial void LogRetryScheduled(ILogger logger, Guid messageId, string messageType, int attempt, string error, DateTimeOffset nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({MessageType}) dead-lettered after {Attempt} attempts; last error {Error}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string messageType, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} processed outbox messages past retention")]
    private static partial void LogCleanedUp(ILogger logger, int count);
}
