using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Billing;

[Trait("Story", "P2-F8")]
public sealed class BillingRulesTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();

    private static YearMonth M(int year, int month) => YearMonth.Create(year, month).Value;

    [Fact]
    public void A_version_can_start_in_the_current_or_a_later_period()
    {
        var current = BillingRules.Define(School, new BillingRuleTerms(M(2027, 1), 10, 15, true), M(2027, 1));
        var later = BillingRules.Define(School, new BillingRuleTerms(M(2027, 6), 0, 0, false), M(2027, 1));

        current.IsSuccess.ShouldBeTrue();
        current.Value.Terms.ShouldBe(new BillingRuleTerms(M(2027, 1), 10, 15, true));
        current.Value.TenantId.ShouldBe(School);
        later.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_past_period_is_refused_like_a_fee()
    {
        BillingRules.Define(School, new BillingRuleTerms(M(2026, 12), 10, 15, true), M(2027, 1)).Error.ShouldBe(BillingRulesErrors.PeriodInPast);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(101, 10)]
    [InlineData(10, 101)]
    public void Percents_are_between_0_and_100(int second, int third)
    {
        BillingRules.Define(School, new BillingRuleTerms(M(2027, 1), second, third, true), M(2027, 1)).Error.ShouldBe(BillingRulesErrors.InvalidPercent);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void Percent_bounds_are_valid(int second, int third)
    {
        BillingRules.Define(School, new BillingRuleTerms(M(2027, 1), second, third, true), M(2027, 1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_version_in_force_is_the_latest_not_after_the_period()
    {
        BillingRuleTerms[] versions = [new(M(2027, 3), 10, 15, true), new(M(2027, 1), 5, 5, false)];

        BillingRuleTerms.For(versions, M(2026, 12)).ShouldBeNull();
        BillingRuleTerms.For(versions, M(2027, 1))!.SecondChildPercent.ShouldBe(5);
        BillingRuleTerms.For(versions, M(2027, 2))!.SecondChildPercent.ShouldBe(5);
        BillingRuleTerms.For(versions, M(2027, 3))!.SecondChildPercent.ShouldBe(10);
        BillingRuleTerms.For(versions, M(2030, 1))!.SecondChildPercent.ShouldBe(10);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 10)]
    [InlineData(3, 15)]
    [InlineData(7, 15)]
    public void The_percent_depends_on_the_rank(int rank, int percent)
    {
        new BillingRuleTerms(M(2027, 1), 10, 15, true).PercentForRank(rank).ShouldBe(percent);
    }
}

[Trait("Story", "P2-F8")]
public sealed class BillingRulesWithdrawalAndClosingTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();

    private static YearMonth M(int year, int month) => YearMonth.Create(year, month).Value;

    [Fact]
    public void Only_a_version_that_has_not_started_can_be_withdrawn()
    {
        var planned = BillingRules.Define(School, new BillingRuleTerms(M(2027, 3), 10, 15, true), M(2027, 1)).Value;
        var started = BillingRules.Define(School, new BillingRuleTerms(M(2027, 1), 10, 15, true), M(2027, 1)).Value;

        planned.EnsureWithdrawable(M(2027, 2)).IsSuccess.ShouldBeTrue();
        planned.EnsureWithdrawable(M(2027, 3)).Error.ShouldBe(BillingRulesErrors.AlreadyStarted);
        started.EnsureWithdrawable(M(2027, 1)).Error.ShouldBe(BillingRulesErrors.AlreadyStarted);
    }

    [Fact]
    public void A_snapshot_freezes_the_charge_and_only_a_past_period_can_close()
    {
        var enrollment = new SporAkademi.Domain.Billing.DueEnrollment(EntityId.New<SporAkademi.Domain.Enrollments.EnrollmentId>(), EntityId.New<SporAkademi.Domain.Groups.GroupId>(), new DateOnly(2027, 1, 4), null);
        var charge = new EffectiveCharge(enrollment, EntityId.New<SporAkademi.Domain.Students.StudentId>(), M(2027, 1), Money.Try(800m), Money.Try(720m), ChargeAdjustment.SiblingDiscount, 10, 2);

        var snapshot = ChargeSnapshot.Take(School, charge);

        (snapshot.ListFee.Amount, snapshot.Discount.Amount, snapshot.Fee.Amount, snapshot.DiscountPercent).ShouldBe((800m, 80m, 720m, 10));
        (snapshot.ToCharge(enrollment) with { SiblingRank = 2 }).ShouldBe(charge);
        Should.Throw<ArgumentException>(() => snapshot.ToCharge(enrollment with { Id = EntityId.New<SporAkademi.Domain.Enrollments.EnrollmentId>() }));
        ClosedPeriod.Close(School, M(2027, 1), M(2027, 2), DateTimeOffset.UnixEpoch, 1).Period.ShouldBe(M(2027, 1));
        Should.Throw<InvalidOperationException>(() => ClosedPeriod.Close(School, M(2027, 2), M(2027, 2), DateTimeOffset.UnixEpoch, 0));
    }
}
