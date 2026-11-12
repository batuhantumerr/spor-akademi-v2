using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>
/// Revenue figures for the School Admin reports (P3-09, DEC-408), from the same effective charges
/// (<see cref="DueCharges"/>: fee rules and frozen closed months) and the same payment totals as the dues list, so a
/// period's "outstanding" here is exactly the sum of <c>GET /dues/outstanding?period=</c>. A fixed number of queries,
/// independent of the school's size: the charges of the dues window, the payment totals per enrollment and period, and
/// the collected totals per payment month.
/// </summary>
internal sealed class BillingReportService(DueCharges dueCharges, IPaymentRepository payments, DuesReminderSettings settings) : IBillingReport
{
    /// <summary>Lower bounds of the ageing buckets in days past the due date (1–30, 31–60, 61–90, 91+).</summary>
    public static readonly int[] BucketStarts = [1, 31, 61, 91];

    public async Task<RevenueFigures> RevenueAsync(YearMonth firstPeriod, YearMonth lastPeriod, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firstPeriod);
        ArgumentNullException.ThrowIfNull(lastPeriod);
        var (from, to) = (firstPeriod, lastPeriod);

        var currentPeriod = YearMonth.From(today);
        var horizon = OutstandingDues.HorizonStart(currentPeriod);
        if (from < horizon || to > currentPeriod || from > to)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPeriod), "The report window must lie within the dues window.");
        }

        // The whole dues window at once: the ageing needs it, and the report's periods are a part of it.
        var book = await dueCharges.ForSchoolAsync(horizon, currentPeriod, cancellationToken);
        var paid = await payments.RecordedTotalsAsync(null, horizon, currentPeriod, cancellationToken);
        var collected = await payments.CollectedByPaymentMonthAsync(from.FirstDay, to.LastDay, cancellationToken);
        var dues = FeeRules.Outstanding(book.Charges, paid);

        var chargesOf = book.Charges.ToLookup(c => c.Period);
        var paidOf = paid.GroupBy(p => p.Key.Period).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        var remainingOf = dues.GroupBy(d => d.Charge.Period).ToDictionary(g => g.Key, g => g.Sum(d => d.Remaining.Amount));

        var periods = new List<PeriodRevenue>();
        for (var period = from; period <= to; period = period.Next())
        {
            var charges = chargesOf[period].ToList();
            var sibling = charges.Where(c => c.Adjustment == ChargeAdjustment.SiblingDiscount).ToList();
            periods.Add(new PeriodRevenue(
                period,
                charges.Sum(c => c.ListFee.Amount),
                sibling.Sum(c => c.Discount.Amount),
                charges.Where(c => c.Adjustment == ChargeAdjustment.GroupChange).Sum(c => c.Discount.Amount),
                charges.Sum(c => c.Fee.Amount),
                paidOf.GetValueOrDefault(period),
                remainingOf.GetValueOrDefault(period),
                collected.GetValueOrDefault(period),
                charges.Count(c => c.Fee.Amount > 0),
                sibling.Count));
        }

        // DEC-408: a due is overdue from the day after its due date (DEC-355's reminder schedule).
        var schedule = settings.Schedule;
        var buckets = BucketStarts.Select(_ => (Dues: 0, Amount: 0m)).ToArray();
        var notYetDue = (Dues: 0, Amount: 0m);
        foreach (var due in dues)
        {
            var late = today.DayNumber - schedule.DueDateOf(due.Charge.Period).DayNumber;
            if (late <= 0)
            {
                notYetDue = (notYetDue.Dues + 1, notYetDue.Amount + due.Remaining.Amount);
                continue;
            }

            var index = Array.FindLastIndex(BucketStarts, start => late >= start);
            buckets[index] = (buckets[index].Dues + 1, buckets[index].Amount + due.Remaining.Amount);
        }

        var overdue = BucketStarts
            .Select((start, i) => new OverdueBucket(
                start,
                i + 1 < BucketStarts.Length ? BucketStarts[i + 1] - 1 : null,
                buckets[i].Dues,
                buckets[i].Amount))
            .ToList();

        return new RevenueFigures(periods, overdue, notYetDue.Dues, notYetDue.Amount, schedule.DueDay);
    }
}
