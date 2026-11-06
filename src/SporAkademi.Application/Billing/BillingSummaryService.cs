using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>
/// Billing figures for the dashboard, computed with the same inputs and the same <see cref="DueCalculator"/> as
/// <c>GET /dues/outstanding</c> (DEC-183), so the two never disagree. A fixed number of queries: enrollments of the
/// window, their fees, payment totals per enrollment and period, and collected totals per payment month.
/// </summary>
internal sealed class BillingSummaryService(
    IEnrollmentDirectory enrollments,
    IGroupFeeSchedule fees,
    IPaymentRepository payments) : IBillingSummary
{
    public async Task<BillingSummary> SummarizeAsync(YearMonth currentPeriod, int months, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThan(months, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(months, ListOutstandingDuesHandler.DuesHorizonMonths);

        var from = YearMonth.From(currentPeriod.FirstDay.AddMonths(1 - ListOutstandingDuesHandler.DuesHorizonMonths));
        var active = await enrollments.ListActiveBetweenAsync(null, from.FirstDay, currentPeriod.LastDay, cancellationToken);
        var groupFees = await fees.GetFeesAsync([.. active.Select(e => e.GroupId).Distinct()], cancellationToken);
        var rates = groupFees.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<FeeRate>)[.. g.Value.Select(f => new FeeRate(f.EffectiveFrom, f.Amount))]);
        var paid = await payments.RecordedTotalsAsync(null, from, currentPeriod, cancellationToken);

        var dueEnrollments = active.Select(e => new DueEnrollment(e.Id, e.GroupId, e.StartDate, e.EndDate)).ToList();
        var outstanding = DueCalculator.Outstanding(dueEnrollments, rates, paid, currentPeriod, from);
        var charges = DueCalculator.Charges(dueEnrollments, rates, currentPeriod, from)
            .GroupBy(c => c.Period)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Fee.Amount));

        var firstSeriesPeriod = YearMonth.From(currentPeriod.FirstDay.AddMonths(1 - months));
        var collected = await payments.CollectedByPaymentMonthAsync(firstSeriesPeriod.FirstDay, currentPeriod.LastDay, cancellationToken);

        var series = new List<MonthlyBilling>(months);
        for (var period = firstSeriesPeriod; period <= currentPeriod; period = period.Next())
        {
            series.Add(new MonthlyBilling(period, collected.GetValueOrDefault(period), charges.GetValueOrDefault(period)));
        }

        return new BillingSummary(
            collected.GetValueOrDefault(currentPeriod),
            outstanding.Where(d => d.Period == currentPeriod).Sum(d => d.Remaining.Amount),
            outstanding.Sum(d => d.Remaining.Amount),
            series);
    }
}
