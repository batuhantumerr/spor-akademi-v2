using System.Diagnostics;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// Adds the message to the unit of work's change tracker; the unit of work saves it with the business change and
/// commits both or neither (DEC-300). Outside a transaction it refuses, because the guarantee would be lost.
/// </summary>
internal sealed class EfOutbox(AppDbContext dbContext, OutboxMessageTypes types, TimeProvider clock, ICurrentUser currentUser) : IOutbox
{
    public void Enqueue<TMessage>(TMessage message, Guid? tenantId)
        where TMessage : class, IOutboxMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Outbox messages are written inside the business change's transaction (IUnitOfWork).");
        }

        // ADR-0003 layer 2 for messages: a school's request only produces messages for its own school.
        if (currentUser.TenantId is { } current && tenantId != current)
        {
            throw new TenantIsolationException("Cannot enqueue an outbox message for another school.");
        }

        var type = types.Get(message.GetType());

        // V1-04 (DEC-695): the delivery, possibly minutes later in another process, continues the request's trace.
        string? traceParent = null;
        string? traceState = null;
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { Length: <= OutboxMessage.TraceParentMaxLength } id } span)
        {
            traceParent = id;
            var state = span.TraceStateString;
            traceState = string.IsNullOrEmpty(state) || state.Length > OutboxMessage.TraceStateMaxLength ? null : state;
        }

        dbContext.Add(new OutboxMessage(Guid.CreateVersion7(), type.Name, type.Serialize(message), tenantId, clock.GetUtcNow(), traceParent, traceState));
    }
}
