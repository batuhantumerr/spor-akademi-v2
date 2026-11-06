namespace SporAkademi.Domain.SharedKernel;

/// <summary>Non-generic view used by the persistence layer to collect domain events.</summary>
public interface IAggregateRoot
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>The single entry point to a consistency boundary. Only the aggregate itself raises events.</summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id), IAggregateRoot
    where TId : struct, IEntityId<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
