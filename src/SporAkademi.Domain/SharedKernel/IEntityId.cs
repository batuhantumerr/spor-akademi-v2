namespace SporAkademi.Domain.SharedKernel;

/// <summary>A strongly typed identifier backed by a UUID v7.</summary>
public interface IEntityId<TSelf>
    where TSelf : struct, IEntityId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

public static class EntityId
{
    /// <summary>
    /// Creates a new UUID v7 id. The embedded timestamp is only used for index-friendly ordering,
    /// so this is the one sanctioned exception to the TimeProvider rule.
    /// </summary>
    public static TId New<TId>()
        where TId : struct, IEntityId<TId> => TId.From(Guid.CreateVersion7());
}
