using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Domain.UnitTests.Billing;

[Trait("Story", "P2-F8")]
public sealed class FeeRulesTests
{
    private static readonly GroupId U10 = EntityId.New<GroupId>();
    private static readonly GroupId U12 = EntityId.New<GroupId>();
    private static readonly YearMonth January = M(2027, 1);

    private static YearMonth M(int year, int month) => YearMonth.Create(year, month).Value;

    private static DateOnly Date(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static StudentId NewStudent() => EntityId.New<StudentId>();

    private static StudentCharge Charge(StudentId student, decimal fee, string start = "2027-01-01", string? end = null, YearMonth? period = null, GroupId? group = null) =>
        new(new DueEnrollment(EntityId.New<EnrollmentId>(), group ?? U10, Date(start), end is null ? null : Date(end)), student, period ?? January, Money.Try(fee));

    private static BillingRuleTerms Rules(int second = 10, int third = 15, bool singleCharge = true, YearMonth? from = null) =>
        new(from ?? January, second, third, singleCharge);

    private static Dictionary<StudentId, StudentId> Family(params StudentId[] students) =>
        students.ToDictionary(s => s, _ => students[0]);

    private static IReadOnlyList<EffectiveCharge> Apply(IEnumerable<StudentCharge> charges, IReadOnlyDictionary<StudentId, StudentId>? families = null, params BillingRuleTerms[] versions) =>
        FeeRules.Apply(charges, families ?? new Dictionary<StudentId, StudentId>(), versions);

    [Fact]
    public void Without_rules_every_charge_is_the_groups_fee_unchanged()
    {
        var (ali, veli) = (NewStudent(), NewStudent());
        List<StudentCharge> charges = [Charge(ali, 1000m), Charge(veli, 800m), Charge(ali, 500m, start: "2027-01-20", group: U12)];

        var result = Apply(charges, Family(ali, veli));

        result.Select(c => (c.Fee.Amount, c.Discount.Amount, c.Adjustment)).ShouldBe([(1000m, 0m, ChargeAdjustment.None), (800m, 0m, ChargeAdjustment.None), (500m, 0m, ChargeAdjustment.None)]);
        result.Select(c => c.ListFee).ShouldBe(charges.Select(c => c.ListFee));
    }

    [Fact]
    public void Without_rules_the_result_matches_the_due_calculator()
    {
        // The rules engine must not change anything for a school that never defines rules (DEC-370).
        var enrollments = new[]
        {
            new DueEnrollment(EntityId.New<EnrollmentId>(), U10, Date("2026-11-30"), Date("2027-01-01")),
            new DueEnrollment(EntityId.New<EnrollmentId>(), U12, Date("2027-01-15"), null),
        };
        var fees = new Dictionary<GroupId, IReadOnlyList<FeeRate>>
        {
            [U10] = [new FeeRate(M(2026, 1), Money.Try(1000m))],
            [U12] = [new FeeRate(M(2026, 1), Money.Try(750.55m))],
        };
        var paid = new Dictionary<(EnrollmentId, YearMonth), decimal> { [(enrollments[0].Id, M(2026, 12))] = 400m };
        var student = NewStudent();

        var raw = DueCalculator.Charges(enrollments, fees, M(2027, 2)).Select(c => new StudentCharge(c.Enrollment, student, c.Period, c.Fee));
        var withRules = FeeRules.Outstanding(Apply(raw, Family(student)), paid);
        var plain = DueCalculator.Outstanding(enrollments, fees, paid, M(2027, 2));

        withRules.Select(d => (d.Charge.Enrollment.Id, d.Charge.Period, d.Charge.Fee, d.Paid, d.Remaining))
            .ShouldBe(plain.Select(d => (d.EnrollmentId, d.Period, d.Fee, d.Paid, d.Remaining)));
    }

    [Fact]
    public void The_family_member_with_the_highest_fee_pays_in_full_and_the_others_get_the_rank_discount()
    {
        var (ali, veli, can) = (NewStudent(), NewStudent(), NewStudent());

        var result = Apply([Charge(ali, 800m), Charge(veli, 1000m), Charge(can, 600m)], Family(ali, veli, can), Rules(second: 10, third: 15));

        result.Select(c => (c.SiblingRank, c.DiscountPercent, c.Fee.Amount, c.Discount.Amount, c.Adjustment)).ShouldBe([
            (2, 10, 720m, 80m, ChargeAdjustment.SiblingDiscount),
            (1, 0, 1000m, 0m, ChargeAdjustment.None),
            (3, 15, 510m, 90m, ChargeAdjustment.SiblingDiscount),
        ]);
    }

    [Fact]
    public void Fourth_and_later_siblings_get_the_third_child_percent()
    {
        var students = Enumerable.Range(0, 5).Select(_ => NewStudent()).ToArray();

        var result = Apply(students.Select((s, i) => Charge(s, 1000m - (i * 100m))), Family(students), Rules(second: 10, third: 20));

        result.Select(c => c.DiscountPercent).ShouldBe([0, 10, 20, 20, 20]);
    }

    [Fact]
    public void Ranking_uses_the_students_total_over_all_their_enrollments_of_the_month()
    {
        var (ali, veli) = (NewStudent(), NewStudent());

        // Ali has two groups (600 + 500 = 1100), Veli one at 1000: Ali pays in full although Veli's single fee is higher.
        var result = Apply([Charge(ali, 600m), Charge(ali, 500m, group: U12), Charge(veli, 1000m)], Family(ali, veli), Rules(second: 10));

        result.Select(c => c.Fee.Amount).ShouldBe([600m, 500m, 900m]);
    }

    [Fact]
    public void Every_charge_of_a_discounted_sibling_is_discounted()
    {
        var (ali, veli) = (NewStudent(), NewStudent());

        var result = Apply([Charge(ali, 2000m), Charge(veli, 600m), Charge(veli, 400m, group: U12)], Family(ali, veli), Rules(second: 25));

        result.Select(c => c.Fee.Amount).ShouldBe([2000m, 450m, 300m]);
    }

    [Fact]
    public void Equal_totals_are_ranked_by_the_earliest_enrollment_start()
    {
        var (early, late) = (NewStudent(), NewStudent());

        var result = Apply([Charge(late, 1000m, start: "2026-09-01"), Charge(early, 1000m, start: "2026-03-01")], Family(late, early), Rules(second: 10));

        result.Select(c => (c.SiblingRank, c.Fee.Amount)).ShouldBe([(2, 900m), (1, 1000m)]);
    }

    [Fact]
    public void Equal_totals_and_starts_are_ranked_by_student_id()
    {
        var first = StudentId.From(Guid.Parse("00000000-0000-7000-8000-000000000001"));
        var second = StudentId.From(Guid.Parse("00000000-0000-7000-8000-000000000002"));

        var result = Apply([Charge(second, 1000m), Charge(first, 1000m)], Family(second, first), Rules(second: 10));

        result.Select(c => (c.StudentId, c.SiblingRank)).ShouldBe([(second, 2), (first, 1)]);
    }

    [Theory]
    [InlineData(333.33, 10, 300.00)] // 299.997 rounds up
    [InlineData(100.05, 50, 50.03)] // 50.025: half away from zero, not to even
    [InlineData(100.01, 15, 85.01)] // 85.0085
    [InlineData(999.99, 0, 999.99)]
    [InlineData(999.99, 100, 0.00)]
    public void Discounted_fees_are_rounded_to_kurus_half_away_from_zero(double listFee, int percent, double expected)
    {
        var (ali, veli) = (NewStudent(), NewStudent());

        var result = Apply([Charge(ali, 5000m), Charge(veli, (decimal)listFee)], Family(ali, veli), Rules(second: percent));

        result[1].Fee.Amount.ShouldBe((decimal)expected);
        result[1].Discount.Amount.ShouldBe((decimal)listFee - (decimal)expected);
    }

    [Fact]
    public void A_zero_percent_is_no_discount()
    {
        var (ali, veli, can) = (NewStudent(), NewStudent(), NewStudent());

        var result = Apply([Charge(ali, 1000m), Charge(veli, 900m), Charge(can, 800m)], Family(ali, veli, can), Rules(second: 0, third: 0));

        result.ShouldAllBe(c => c.Adjustment == ChargeAdjustment.None && c.Fee == c.ListFee);
    }

    [Fact]
    public void A_full_discount_owes_nothing_and_is_not_outstanding()
    {
        var (ali, veli) = (NewStudent(), NewStudent());

        var result = Apply([Charge(ali, 1000m), Charge(veli, 900m)], Family(ali, veli), Rules(second: 100));

        result[1].Fee.ShouldBe(Money.Zero);
        result[1].Discount.Amount.ShouldBe(900m);
        FeeRules.Outstanding(result, new Dictionary<(EnrollmentId, YearMonth), decimal>()).ShouldHaveSingleItem().Charge.StudentId.ShouldBe(ali);
    }

    [Fact]
    public void Students_without_siblings_and_other_families_pay_in_full()
    {
        var (ali, veli, alone, other1, other2) = (NewStudent(), NewStudent(), NewStudent(), NewStudent(), NewStudent());
        var families = new Dictionary<StudentId, StudentId>(Family(ali, veli));
        foreach (var (key, value) in Family(other1, other2))
        {
            families[key] = value;
        }

        var result = Apply([Charge(ali, 1000m), Charge(veli, 900m), Charge(alone, 700m), Charge(other1, 500m), Charge(other2, 400m)], families, Rules(second: 10));

        result.Select(c => c.Fee.Amount).ShouldBe([1000m, 810m, 700m, 500m, 360m]);
    }

    [Fact]
    public void A_sibling_without_a_charge_that_month_does_not_take_a_rank()
    {
        var (ali, veli) = (NewStudent(), NewStudent());
        var gone = NewStudent();

        // The third sibling has no charge in January (no enrollment, or only a free group): Veli is still the second child.
        var result = Apply([Charge(ali, 1000m), Charge(gone, 0m), Charge(veli, 900m)], Family(ali, veli, gone), Rules(second: 10, third: 50));

        result.Select(c => (c.Fee.Amount, c.SiblingRank)).ShouldBe([(1000m, 1), (0m, null), (810m, 2)]);
    }

    [Fact]
    public void Ranks_are_computed_per_period()
    {
        var (ali, veli) = (NewStudent(), NewStudent());

        // Ali is the expensive child in January, Veli in February.
        var result = Apply(
            [Charge(ali, 1000m), Charge(veli, 800m), Charge(ali, 600m, period: M(2027, 2)), Charge(veli, 800m, period: M(2027, 2))],
            Family(ali, veli),
            Rules(second: 10));

        result.Select(c => c.Fee.Amount).ShouldBe([1000m, 720m, 540m, 800m]);
    }

    [Fact]
    public void A_version_applies_from_its_period_until_the_next_one()
    {
        var (ali, veli) = (NewStudent(), NewStudent());
        var versions = new[] { Rules(second: 10, from: M(2027, 2)), Rules(second: 20, from: M(2027, 4)) };
        var periods = new[] { January, M(2027, 2), M(2027, 3), M(2027, 4), M(2027, 5) };

        var result = Apply(periods.SelectMany(p => new[] { Charge(ali, 1000m, period: p), Charge(veli, 500m, period: p) }), Family(ali, veli), versions);

        // Before the first version (January) nothing changes: past months keep their bill.
        result.Where(c => c.StudentId == veli).Select(c => (c.Period, c.Fee.Amount)).ShouldBe([
            (January, 500m),
            (M(2027, 2), 450m),
            (M(2027, 3), 450m),
            (M(2027, 4), 400m),
            (M(2027, 5), 400m),
        ]);
    }

    [Fact]
    public void A_sequential_group_change_charges_the_month_once()
    {
        var ali = NewStudent();

        var result = Apply([Charge(ali, 1000m, start: "2026-09-01", end: "2027-01-10"), Charge(ali, 1200m, start: "2027-01-11", group: U12)], null, Rules());

        result.Select(c => (c.Fee.Amount, c.Discount.Amount, c.Adjustment)).ShouldBe([(1000m, 0m, ChargeAdjustment.None), (0m, 1200m, ChargeAdjustment.GroupChange)]);
    }

    [Fact]
    public void A_same_day_group_change_charges_the_month_once()
    {
        var ali = NewStudent();

        // DEC-170: ending on the 15th and starting the new group on the 15th is a hand-over, not an overlap.
        var result = Apply([Charge(ali, 1200m, start: "2027-01-15", group: U12), Charge(ali, 1000m, start: "2026-09-01", end: "2027-01-15")], null, Rules());

        result.Select(c => c.Adjustment).ShouldBe([ChargeAdjustment.GroupChange, ChargeAdjustment.None]);
        result.Select(c => c.Fee.Amount).ShouldBe([0m, 1000m]);
    }

    [Fact]
    public void A_later_return_in_the_same_month_is_not_charged_again()
    {
        var ali = NewStudent();

        var result = Apply([Charge(ali, 1000m, start: "2026-09-01", end: "2027-01-03"), Charge(ali, 1000m, start: "2027-01-25")], null, Rules());

        result.Select(c => c.Fee.Amount).ShouldBe([1000m, 0m]);
    }

    [Fact]
    public void Overlapping_enrollments_are_both_charged()
    {
        var ali = NewStudent();

        // In two groups at once from the 10th to the 20th.
        var result = Apply([Charge(ali, 1000m, start: "2026-09-01", end: "2027-01-20"), Charge(ali, 800m, start: "2027-01-10", group: U12)], null, Rules());

        result.ShouldAllBe(c => c.Adjustment == ChargeAdjustment.None && c.Fee == c.ListFee);
    }

    [Fact]
    public void A_group_change_only_affects_the_month_of_the_change()
    {
        var ali = NewStudent();
        var february = M(2027, 2);

        var result = Apply(
            [
                Charge(ali, 1000m, start: "2026-09-01", end: "2027-01-10"),
                Charge(ali, 1200m, start: "2027-01-11", group: U12),
                Charge(ali, 1200m, start: "2027-01-11", group: U12, period: february),
            ],
            null,
            Rules());

        result.Select(c => c.Fee.Amount).ShouldBe([1000m, 0m, 1200m]);
    }

    [Fact]
    public void Without_the_group_change_rule_both_enrollments_are_charged()
    {
        var ali = NewStudent();

        var result = Apply([Charge(ali, 1000m, end: "2027-01-10"), Charge(ali, 1200m, start: "2027-01-11")], null, Rules(singleCharge: false));

        result.Select(c => c.Fee.Amount).ShouldBe([1000m, 1200m]);
    }

    [Fact]
    public void A_free_earlier_group_does_not_bill_the_month()
    {
        var ali = NewStudent();

        var result = Apply([Charge(ali, 0m, end: "2027-01-10"), Charge(ali, 1200m, start: "2027-01-11")], null, Rules());

        result.Select(c => c.Fee.Amount).ShouldBe([0m, 1200m]);
    }

    [Fact]
    public void A_chain_of_changes_in_one_month_is_charged_once()
    {
        var ali = NewStudent();

        var result = Apply(
            [
                Charge(ali, 900m, start: "2026-09-01", end: "2027-01-05"),
                Charge(ali, 1000m, start: "2027-01-05", end: "2027-01-20"),
                Charge(ali, 1100m, start: "2027-01-20"),
            ],
            null,
            Rules());

        result.Select(c => c.Fee.Amount).ShouldBe([900m, 0m, 0m]);
    }

    [Fact]
    public void Two_one_day_enrollments_on_the_same_day_charge_once()
    {
        var ali = NewStudent();

        var result = Apply([Charge(ali, 1000m, start: "2027-01-10", end: "2027-01-10"), Charge(ali, 1000m, start: "2027-01-10", end: "2027-01-10")], null, Rules());

        result.Count(c => c.Adjustment == ChargeAdjustment.GroupChange).ShouldBe(1);
    }

    [Fact]
    public void A_waived_enrollment_does_not_count_for_the_sibling_ranking()
    {
        var (ali, veli) = (NewStudent(), NewStudent());

        // Ali moved from a 600 group to a 1500 group; only the 600 is charged, so Veli (1000) is the first child.
        var result = Apply(
            [Charge(ali, 600m, start: "2026-09-01", end: "2027-01-10"), Charge(ali, 1500m, start: "2027-01-11", group: U12), Charge(veli, 1000m)],
            Family(ali, veli),
            Rules(second: 10));

        result.Select(c => (c.Fee.Amount, c.Adjustment)).ShouldBe([
            (540m, ChargeAdjustment.SiblingDiscount),
            (0m, ChargeAdjustment.GroupChange),
            (1000m, ChargeAdjustment.None),
        ]);
    }

    [Fact]
    public void A_sibling_added_to_a_closed_month_ranks_after_the_frozen_siblings()
    {
        var (frozen, late, alsoLate) = (NewStudent(), NewStudent(), NewStudent());
        var frozenCharge = Charge(frozen, 1000m) with { Frozen = true };

        // The late siblings have higher fees but cannot take rank 1 from the frozen child (who paid in full).
        var result = Apply([frozenCharge, Charge(late, 1200m), Charge(alsoLate, 1500m)], Family(frozen, late, alsoLate), Rules(second: 10, third: 15));

        result.Select(c => (c.StudentId, c.SiblingRank, c.Fee.Amount)).ShouldBe([(frozen, 1, 1000m), (late, 3, 1020m), (alsoLate, 2, 1350m)]);
    }

    [Fact]
    public void Arguments_are_validated()
    {
        Should.Throw<ArgumentNullException>(() => FeeRules.Apply(null!, new Dictionary<StudentId, StudentId>(), []));
        Should.Throw<ArgumentNullException>(() => FeeRules.Apply([], null!, []));
        Should.Throw<ArgumentNullException>(() => FeeRules.Apply([], new Dictionary<StudentId, StudentId>(), null!));
        Should.Throw<ArgumentOutOfRangeException>(() => FeeRules.Discounted(Money.Try(10m), 101));
        Should.Throw<ArgumentOutOfRangeException>(() => FeeRules.Discounted(Money.Try(10m), -1));
    }

    [Fact]
    public void Outstanding_subtracts_payments_from_the_owed_fee_and_skips_settled_charges()
    {
        var (ali, veli) = (NewStudent(), NewStudent());
        var charges = Apply([Charge(ali, 1000m), Charge(veli, 900m)], Family(ali, veli), Rules(second: 10));
        var paid = new Dictionary<(EnrollmentId, YearMonth), decimal>
        {
            [(charges[0].Enrollment.Id, January)] = 1000m,
            [(charges[1].Enrollment.Id, January)] = 500m,
        };

        var due = FeeRules.Outstanding(charges, paid).ShouldHaveSingleItem();

        (due.Charge.Fee.Amount, due.Paid.Amount, due.Remaining.Amount).ShouldBe((810m, 500m, 310m));
    }
}
