using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>
/// One fee period of the revenue report (P3-09, DEC-408). Amounts are TRY and exact decimals.
/// <list type="bullet">
/// <item><see cref="ListFees"/>: the groups' fees before any fee rule.</item>
/// <item><see cref="SiblingDiscount"/>: what the sibling discount took off (DEC-371).</item>
/// <item><see cref="GroupChangeWaived"/>: the fees not charged for the month of a group change (DEC-372).</item>
/// <item><see cref="Accrued"/>: what is owed after the fee rules (tahakkuk); the dashboard's "due".</item>
/// <item><see cref="CollectedForPeriod"/>: Recorded payments made for the period, whenever they were paid.</item>
/// <item><see cref="Outstanding"/>: what the dues list shows as remaining for the period (<c>GET /dues/outstanding?period=</c>).</item>
/// <item><see cref="CollectedInMonth"/>: Recorded payments whose payment date falls in the month (cash basis, like the dashboard).</item>
/// </list>
/// </summary>
public sealed record PeriodRevenue(
    YearMonth Period,
    decimal ListFees,
    decimal SiblingDiscount,
    decimal GroupChangeWaived,
    decimal Accrued,
    decimal CollectedForPeriod,
    decimal Outstanding,
    decimal CollectedInMonth,
    int Charges,
    int SiblingDiscountedCharges);

/// <summary>Unpaid dues whose due date passed <see cref="FromDays"/> to <see cref="ToDays"/> days ago (no upper bound when null).</summary>
public sealed record OverdueBucket(int FromDays, int? ToDays, int Dues, decimal Amount);

/// <summary>The revenue report: the periods asked for, and the ageing of today's unpaid dues over the dues window.</summary>
public sealed record RevenueFigures(
    IReadOnlyList<PeriodRevenue> Periods,
    IReadOnlyList<OverdueBucket> Overdue,
    int NotYetDueDues,
    decimal NotYetDueAmount,
    int DueDay);

/// <summary>
/// Read contract of the Billing module for the School Admin reports (P3-09). The amounts come from the same effective
/// charges as the dues list and the dashboard (<c>DueCharges</c>: fee rules, frozen closed months), so the three agree.
/// </summary>
public interface IBillingReport
{
    /// <summary>
    /// Figures for every period from <paramref name="firstPeriod"/> to <paramref name="lastPeriod"/> (inclusive, not after the current
    /// period of <paramref name="today"/>), and the ageing of the dues still unpaid on <paramref name="today"/> within the
    /// dues window (24 months, like <c>GET /dues/outstanding</c>).
    /// </summary>
    Task<RevenueFigures> RevenueAsync(YearMonth firstPeriod, YearMonth lastPeriod, DateOnly today, CancellationToken cancellationToken);
}
