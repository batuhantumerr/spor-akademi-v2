using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>One month of the school's money: what was collected (by payment date) and what was due (by period).</summary>
public sealed record MonthlyBilling(YearMonth Period, decimal Collected, decimal Due);

/// <summary>The school's billing figures for the dashboard (DEC-211). Amounts are TRY.</summary>
public sealed record BillingSummary(
    decimal CollectedThisMonth,
    decimal OutstandingThisMonth,
    decimal OutstandingTotal,
    IReadOnlyList<MonthlyBilling> Months);

/// <summary>Read contract of the Billing module (used by the dashboard). The due rules stay in <c>DueCalculator</c> and <c>FeeRules</c>.</summary>
public interface IBillingSummary
{
    /// <summary>
    /// Figures up to <paramref name="currentPeriod"/>: collected this month, outstanding this month and over the dues
    /// window (24 months, like <c>GET /dues/outstanding</c>), and the last <paramref name="months"/> months oldest first.
    /// </summary>
    Task<BillingSummary> SummarizeAsync(YearMonth currentPeriod, int months, CancellationToken cancellationToken);
}
