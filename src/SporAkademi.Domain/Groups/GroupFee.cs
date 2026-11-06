using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Groups;

public readonly record struct GroupFeeId(Guid Value) : IEntityId<GroupFeeId>
{
    public static GroupFeeId From(Guid value) => new(value);
}

/// <summary>
/// The monthly fee of a group from <see cref="EffectiveFrom"/> until the next fee (appendix B 4.5, US-22).
/// Part of the <see cref="Group"/> aggregate; never changed or removed once defined, so past periods keep their amount.
/// </summary>
public sealed class GroupFee : Entity<GroupFeeId>
{
    private GroupFee(GroupFeeId id, GroupId groupId, YearMonth effectiveFrom, Money amount)
        : base(id)
    {
        GroupId = groupId;
        EffectiveFrom = effectiveFrom;
        Amount = amount;
    }

    // For EF Core materialization only.
    private GroupFee()
        : base(default)
    {
        EffectiveFrom = null!;
        Amount = null!;
    }

    public GroupId GroupId { get; private set; }

    public YearMonth EffectiveFrom { get; private set; }

    public Money Amount { get; private set; }

    internal static GroupFee Define(GroupId groupId, YearMonth effectiveFrom, Money amount) =>
        new(EntityId.New<GroupFeeId>(), groupId, effectiveFrom, amount);
}

public static class FeeErrors
{
    public static readonly Error PeriodInPast = Error.BusinessRule("Fee.PeriodInPast", "A past period's fee cannot change.");
    public static readonly Error AlreadyDefined = Error.Conflict("Fee.AlreadyDefined", "A fee is already defined for this period.");
}
