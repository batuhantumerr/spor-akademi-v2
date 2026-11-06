using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.Billing;

[Trait("Story", "US-24")]
public sealed class DueCalculatorTests
{
    private static readonly GroupId Group = EntityId.New<GroupId>();

    private static YearMonth M(int year, int month) => YearMonth.Create(year, month).Value;

    private static DueEnrollment Enrollment(string start, string? end = null) =>
        new(EntityId.New<EnrollmentId>(), Group, Date(start), end is null ? null : Date(end));

    private static DateOnly Date(string text) => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<GroupId, IReadOnlyList<FeeRate>> Fees(params (YearMonth From, decimal Amount)[] rates) =>
        new() { [Group] = [.. rates.Select(r => new FeeRate(r.From, Money.Try(r.Amount)))] };

    [Fact]
    public void Every_month_with_at_least_one_active_day_owes_the_full_fee()
    {
        // Started on the last day of November, ended on the first day of January: three full months (DEC-017).
        var enrollment = Enrollment("2026-11-30", "2027-01-01");

        var dues = DueCalculator.Outstanding([enrollment], Fees((M(2026, 1), 1000m)), new Dictionary<(EnrollmentId, YearMonth), Money>(), M(2027, 3));

        dues.Select(d => d.Period).ShouldBe([M(2026, 11), M(2026, 12), M(2027, 1)]);
        dues.ShouldAllBe(d => d.Fee == Money.Try(1000m) && d.Remaining == Money.Try(1000m) && d.Paid == Money.Zero);
    }

    [Fact]
    public void Open_enrollments_owe_up_to_the_current_month_only()
    {
        var dues = DueCalculator.Outstanding([Enrollment("2027-01-20")], Fees((M(2027, 1), 500m)), new Dictionary<(EnrollmentId, YearMonth), Money>(), M(2027, 2));

        dues.Select(d => d.Period).ShouldBe([M(2027, 1), M(2027, 2)]);
    }

    [Fact]
    public void Recorded_payments_reduce_the_due_and_full_or_over_payment_clears_it()
    {
        var enrollment = Enrollment("2027-01-01");
        var paid = new Dictionary<(EnrollmentId, YearMonth), Money>
        {
            [(enrollment.Id, M(2027, 1))] = Money.Try(400m),
            [(enrollment.Id, M(2027, 2))] = Money.Try(1000m),
            [(enrollment.Id, M(2027, 3))] = Money.Try(1500m),
        };

        var dues = DueCalculator.Outstanding([enrollment], Fees((M(2027, 1), 1000m)), paid, M(2027, 3));

        var january = dues.ShouldHaveSingleItem();
        january.Period.ShouldBe(M(2027, 1));
        january.Paid.ShouldBe(Money.Try(400m));
        january.Remaining.ShouldBe(Money.Try(600m));
    }

    [Fact]
    public void A_fee_change_mid_year_keeps_past_periods()
    {
        var dues = DueCalculator.Outstanding(
            [Enrollment("2027-01-01")],
            Fees((M(2026, 9), 1000m), (M(2027, 3), 1250m)),
            new Dictionary<(EnrollmentId, YearMonth), Money>(),
            M(2027, 4));

        dues.Select(d => d.Fee.Amount).ShouldBe([1000m, 1000m, 1250m, 1250m]);
    }

    [Fact]
    public void Months_without_a_fee_owe_nothing()
    {
        var dues = DueCalculator.Outstanding([Enrollment("2027-01-01")], Fees((M(2027, 3), 800m)), new Dictionary<(EnrollmentId, YearMonth), Money>(), M(2027, 3));

        dues.Select(d => d.Period).ShouldBe([M(2027, 3)]);
        DueCalculator.Outstanding([Enrollment("2027-01-01")], new Dictionary<GroupId, IReadOnlyList<FeeRate>>(), new Dictionary<(EnrollmentId, YearMonth), Money>(), M(2027, 3))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Fee_lookup_takes_the_latest_rate_not_after_the_period()
    {
        IReadOnlyList<FeeRate> rates = [new(M(2027, 3), Money.Try(2m)), new(M(2027, 1), Money.Try(1m))];

        FeeRate.For(rates, M(2026, 12)).ShouldBeNull();
        FeeRate.For(rates, M(2027, 2)).ShouldBe(Money.Try(1m));
        FeeRate.For(rates, M(2027, 3)).ShouldBe(Money.Try(2m));
    }

    [Theory]
    [InlineData("2027-01-31", null, true)]
    [InlineData("2027-02-01", null, false)]
    [InlineData("2026-12-01", "2027-01-01", true)]
    [InlineData("2026-12-01", "2026-12-31", false)]
    public void Active_in_period_means_at_least_one_day(string start, string? end, bool active)
    {
        DueCalculator.WasActiveIn(Date(start), end is null ? null : Date(end), M(2027, 1)).ShouldBe(active);
    }
}
