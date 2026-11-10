using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct ChargeSnapshotId(Guid Value) : IEntityId<ChargeSnapshotId>
{
    public static ChargeSnapshotId From(Guid value) => new(value);
}

public readonly record struct ClosedPeriodId(Guid Value) : IEntityId<ClosedPeriodId>
{
    public static ClosedPeriodId From(Guid value) => new(value);
}

/// <summary>
/// What one enrollment owed for one closed period under the fee rules (P2-F8, DEC-375): the amounts frozen when the
/// month closed, so later changes to guardian links (unlinking, deletion, anonymization) or new sibling links can never
/// change a closed month, neither up nor down. Ids and amounts only, no personal data. Never changed or deleted.
/// </summary>
public sealed class ChargeSnapshot : AggregateRoot<ChargeSnapshotId>, ITenantScoped
{
    private ChargeSnapshot(ChargeSnapshotId id, SchoolId tenantId, EffectiveCharge charge)
        : base(id)
    {
        TenantId = tenantId;
        EnrollmentId = charge.Enrollment.Id;
        StudentId = charge.StudentId;
        GroupId = charge.Enrollment.GroupId;
        Period = charge.Period;
        ListFee = charge.ListFee;
        Discount = charge.Discount;
        Fee = charge.Fee;
        Adjustment = charge.Adjustment;
        DiscountPercent = charge.DiscountPercent;
    }

    // For EF Core materialization only.
    private ChargeSnapshot()
        : base(default)
    {
        Period = null!;
        ListFee = null!;
        Discount = null!;
        Fee = null!;
    }

    public SchoolId TenantId { get; private set; }

    public EnrollmentId EnrollmentId { get; private set; }

    public StudentId StudentId { get; private set; }

    public GroupId GroupId { get; private set; }

    public YearMonth Period { get; private set; }

    public Money ListFee { get; private set; }

    public Money Discount { get; private set; }

    /// <summary>What was owed (list fee less discount).</summary>
    public Money Fee { get; private set; }

    public ChargeAdjustment Adjustment { get; private set; }

    public int DiscountPercent { get; private set; }

    public static ChargeSnapshot Take(SchoolId tenantId, EffectiveCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        return new ChargeSnapshot(EntityId.New<ChargeSnapshotId>(), tenantId, charge);
    }

    /// <summary>The frozen charge of <paramref name="enrollment"/> (the snapshot's enrollment).</summary>
    public EffectiveCharge ToCharge(DueEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (enrollment.Id != EnrollmentId)
        {
            throw new ArgumentException("The snapshot belongs to another enrollment.", nameof(enrollment));
        }

        return new EffectiveCharge(enrollment, StudentId, Period, ListFee, Fee, Adjustment, DiscountPercent, null);
    }
}

/// <summary>
/// A period of a school whose charges are frozen in <see cref="ChargeSnapshot"/>s (DEC-375). Recorded once, by the
/// daily close job, for periods before the school's current one that a fee rules version applies to.
/// </summary>
public sealed class ClosedPeriod : AggregateRoot<ClosedPeriodId>, ITenantScoped
{
    private ClosedPeriod(ClosedPeriodId id, SchoolId tenantId, YearMonth period, DateTimeOffset closedAt, int snapshotCount)
        : base(id)
    {
        TenantId = tenantId;
        Period = period;
        ClosedAt = closedAt;
        SnapshotCount = snapshotCount;
    }

    // For EF Core materialization only.
    private ClosedPeriod()
        : base(default)
    {
        Period = null!;
    }

    public SchoolId TenantId { get; private set; }

    public YearMonth Period { get; private set; }

    public DateTimeOffset ClosedAt { get; private set; }

    public int SnapshotCount { get; private set; }

    /// <summary>Closes <paramref name="period"/>; only a period before <paramref name="currentPeriod"/> can close.</summary>
    public static ClosedPeriod Close(SchoolId tenantId, YearMonth period, YearMonth currentPeriod, DateTimeOffset closedAt, int snapshotCount)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(currentPeriod);
        if (period >= currentPeriod)
        {
            throw new InvalidOperationException("Only a period before the current one can close.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(snapshotCount);
        return new ClosedPeriod(EntityId.New<ClosedPeriodId>(), tenantId, period, closedAt, snapshotCount);
    }
}
