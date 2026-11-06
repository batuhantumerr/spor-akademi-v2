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
    public void New_ids_are_unique_and_time_ordered()
    {
        var first = EntityId.New<SampleId>();
        var second = EntityId.New<SampleId>();

        first.ShouldNotBe(second);
        string.CompareOrdinal(first.Value.ToString(), second.Value.ToString()).ShouldBeLessThanOrEqualTo(0);
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
