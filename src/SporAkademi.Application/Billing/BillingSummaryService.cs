using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>
/// Billing figures for the dashboard, computed from the same effective charges (<see cref="DueCharges"/>, with the fee
/// rules of P2-F8) as <c>GET /dues/outstanding</c> (DEC-183, DEC-373), so the two never disagree. "Due" is what is owed
/// after discounts. A fixed number of queries: the charges of the window, payment totals per enrollment and period,
/// and collected totals per payment month.
/// </summary>
internal sealed class BillingSummaryService(DueCharges dueCharges, IPaymentRepository payments) : IBillingSummary
{
    public async Task<BillingSummary> SummarizeAsync(YearMonth currentPeriod, int months, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThan(months, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(months, ListOutstandingDuesHandler.DuesHorizonMonths);

        var from = YearMonth.From(currentPeriod.FirstDay.AddMonths(1 - ListOutstandingDuesHandler.DuesHorizonMonths));
        var book = await dueCharges.ForSchoolAsync(from, currentPeriod, cancellationToken);
        var paid = await payments.RecordedTotalsAsync(null, from, currentPeriod, cancellationToken);

        var outstanding = FeeRules.Outstanding(book.Charges, paid);
        var charges = book.Charges
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
            outstanding.Where(d => d.Charge.Period == currentPeriod).Sum(d => d.Remaining.Amount),
            outstanding.Sum(d => d.Remaining.Amount),
            series);
    }
}
