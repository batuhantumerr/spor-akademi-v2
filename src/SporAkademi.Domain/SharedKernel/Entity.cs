namespace SporAkademi.Domain.SharedKernel;

/// <summary>An object with an identity that survives changes to its data. Equality is type + id.</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IEntityId<TId>
{
    protected Entity(TId id) => Id = id;

    public TId Id { get; private init; }

    public bool Equals(Entity<TId>? other) =>
        other is not null && other.GetType() == GetType() && other.Id.Equals(Id);

    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}
