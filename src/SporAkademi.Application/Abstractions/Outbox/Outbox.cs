namespace SporAkademi.Application.Abstractions.Outbox;

/// <summary>
/// An integration message: a side effect that leaves the database (e-mail, webhook, ...) and runs after commit
/// (P2-F3). Messages are sealed records of primitive values (ids, dates, amounts) with an
/// <see cref="OutboxMessageAttribute"/>; they carry ids, never personal data, and handlers read what they need when
/// they run. The JSON shape is a stored contract: add fields compatibly or publish a new versioned name.
/// </summary>
#pragma warning disable CA1040 // Marker interface: the outbox type registry scans for it.
public interface IOutboxMessage;
#pragma warning restore CA1040

/// <summary>The stable name stored with each message, e.g. <c>students.student-left.v1</c>; never the CLR type name.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OutboxMessageAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Writes integration messages into the current unit of work's transaction, so they are stored if and only if the
/// business change commits. Call it from a command handler or an <see cref="Messaging.IDomainEventConsumer{TEvent}"/>.
/// </summary>
public interface IOutbox
{
    /// <param name="message">The message; serialized with System.Text.Json.</param>
    /// <param name="tenantId">The school the message belongs to (handlers run in that school's context); <c>null</c> for platform-level messages.</param>
    void Enqueue<TMessage>(TMessage message, Guid? tenantId)
        where TMessage : class, IOutboxMessage;
}

/// <summary>
/// Delivers one kind of integration message after commit. Delivery is at least once: a handler can run again for the
/// same message (after a failure, a timeout or a crash), so it de-duplicates with <see cref="OutboxMessageContext.MessageId"/>
/// where repeating would be visible. A thrown exception is retried with backoff and dead-lettered after the configured
/// number of attempts; exception messages must not contain personal data (only the exception type is stored and logged).
/// </summary>
public interface IOutboxMessageHandler<in TMessage>
    where TMessage : IOutboxMessage
{
    Task Handle(TMessage message, OutboxMessageContext context, CancellationToken cancellationToken);
}

/// <param name="MessageId">Unique per message and stable across retries: the de-duplication key.</param>
/// <param name="TenantId">The message's school; it is also the current tenant while the handler runs.</param>
/// <param name="OccurredAt">When the business change was made.</param>
/// <param name="Attempt">1 for the first delivery.</param>
public sealed record OutboxMessageContext(Guid MessageId, Guid? TenantId, DateTimeOffset OccurredAt, int Attempt);
