using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Groups;

[Trait("Story", "US-22")]
public sealed class GroupFeeTests
{
    private static readonly YearMonth January = YearMonth.Create(2027, 1).Value;
    private static readonly YearMonth March = YearMonth.Create(2027, 3).Value;

    private static Group NewGroup() =>
        Group.Create(
            EntityId.New<SchoolId>(),
            EntityId.New<SportId>(),
            EntityId.New<FacilityId>(),
            null,
            "U10 Futbol",
            BirthYearRange.Create(2015, 2016).Value,
            20).Value;

    [Fact]
    public void A_fee_applies_from_its_month_onward_and_earlier_months_keep_the_old_amount()
    {
        var group = NewGroup();
        group.DefineFee(January, Money.Try(1000m), currentPeriod: January).IsSuccess.ShouldBeTrue();
        group.DefineFee(March, Money.Try(1250m), currentPeriod: January).IsSuccess.ShouldBeTrue();

        group.FeeFor(YearMonth.Create(2026, 12).Value).ShouldBeNull();
        group.FeeFor(January).ShouldBe(Money.Try(1000m));
        group.FeeFor(YearMonth.Create(2027, 2).Value).ShouldBe(Money.Try(1000m));
        group.FeeFor(March).ShouldBe(Money.Try(1250m));
        group.FeeFor(YearMonth.Create(2028, 6).Value).ShouldBe(Money.Try(1250m));
        group.Fees.Select(f => f.EffectiveFrom).ShouldBe([January, March]);
    }

    [Fact]
    public void Past_periods_cannot_get_a_fee()
    {
        var group = NewGroup();

        group.DefineFee(YearMonth.Create(2026, 12).Value, Money.Try(1000m), currentPeriod: January).Error.ShouldBe(FeeErrors.PeriodInPast);
        group.Fees.ShouldBeEmpty();
    }

    [Fact]
    public void One_fee_per_period()
    {
        var group = NewGroup();
        group.DefineFee(March, Money.Try(1000m), currentPeriod: January);

        group.DefineFee(March, Money.Try(900m), currentPeriod: January).Error.ShouldBe(FeeErrors.AlreadyDefined);
        group.FeeFor(March).ShouldBe(Money.Try(1000m));
    }

    [Fact]
    public void Archived_groups_take_no_new_fee()
    {
        var group = NewGroup();
        group.Archive(new DateOnly(2027, 1, 14));

        group.DefineFee(March, Money.Try(1000m), currentPeriod: January).Error.ShouldBe(GroupErrors.Archived);
    }
}
