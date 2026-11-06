using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions.Messaging;

/// <summary>
/// Reacts to a domain event (named Consumer to satisfy CA1711, which reserves the EventHandler suffix) inside the same transaction, before commit (ADR-0009). Only database
/// changes are allowed here; a failure rolls back the whole operation.
/// </summary>
public interface IDomainEventConsumer<in TEvent>
    where TEvent : IDomainEvent
{
    Task<Result> Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
