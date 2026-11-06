using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class EntityTests
{
    [Fact]
    public void New_ids_are_uuid_v7()
    {
        var id = EntityId.New<SampleId>();

        id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void New_ids_are_unique()
    {
        // UUID v7 is only ordered across milliseconds, so ordering is not asserted here.
        var ids = Enumerable.Range(0, 1000).Select(_ => EntityId.New<SampleId>()).ToHashSet();

        ids.Count.ShouldBe(1000);
    }

    [Fact]
    public void Hash_codes_and_inequality_follow_equality()
    {
        var id = EntityId.New<SampleId>();

        new SampleAggregate(id).GetHashCode().ShouldBe(new SampleAggregate(id).GetHashCode());
        (new SampleAggregate(id) != new SampleAggregate(EntityId.New<SampleId>())).ShouldBeTrue();
        new SampleAggregate(id).Equals(null).ShouldBeFalse();
    }

    [Fact]
    public void Entities_with_the_same_id_and_type_are_equal()
    {
        var id = EntityId.New<SampleId>();

        new SampleAggregate(id).ShouldBe(new SampleAggregate(id));
        (new SampleAggregate(id) == new SampleAggregate(id)).ShouldBeTrue();
    }

    [Fact]
    public void Entities_with_different_ids_are_not_equal()
    {
        new SampleAggregate(EntityId.New<SampleId>()).ShouldNotBe(new SampleAggregate(EntityId.New<SampleId>()));
    }

    [Fact]
    public void Entities_of_different_types_with_the_same_id_are_not_equal()
    {
        var id = EntityId.New<SampleId>();

        new SampleAggregate(id).Equals(new OtherAggregate(id)).ShouldBeFalse();
    }

    [Fact]
    public void Aggregate_collects_and_clears_raised_events()
    {
        var aggregate = new SampleAggregate(EntityId.New<SampleId>());

        aggregate.DoSomething();

        aggregate.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<SomethingHappened>();
        aggregate.ClearDomainEvents();
        aggregate.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Ids_can_be_rebuilt_from_a_guid()
    {
        var guid = Guid.CreateVersion7();

        SampleId.From(guid).Value.ShouldBe(guid);
    }
}

public readonly record struct SampleId(Guid Value) : IEntityId<SampleId>
{
    public static SampleId From(Guid value) => new(value);
}

public sealed record SomethingHappened(SampleId Id) : IDomainEvent;

public sealed class SampleAggregate(SampleId id) : AggregateRoot<SampleId>(id)
{
    public void DoSomething() => Raise(new SomethingHappened(Id));
}

public sealed class OtherAggregate(SampleId id) : AggregateRoot<SampleId>(id);
