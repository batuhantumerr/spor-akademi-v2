using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Billing;

/// <summary>A group's fee from <see cref="EffectiveFrom"/> on, as the Groups module reports it.</summary>
public sealed record FeeRate(YearMonth EffectiveFrom, Money Amount)
{
    /// <summary>The latest rate not after <paramref name="period"/>, or null when none applies.</summary>
    public static Money? For(IEnumerable<FeeRate> rates, YearMonth period)
    {
        ArgumentNullException.ThrowIfNull(rates);
        return rates.Where(r => r.EffectiveFrom <= period).MaxBy(r => r.EffectiveFrom)?.Amount;
    }
}

/// <summary>An enrollment's span, as the Enrollments module reports it.</summary>
public sealed record DueEnrollment(EnrollmentId Id, GroupId GroupId, DateOnly StartDate, DateOnly? EndDate);

/// <summary>What an enrollment still owes for one period.</summary>
public sealed record Due(EnrollmentId EnrollmentId, GroupId GroupId, YearMonth Period, Money Fee, Money Paid, Money Remaining);

/// <summary>
/// Computes dues (appendix B §5, US-24). For every period in which the enrollment was active at least one
/// day, up to the current period, the full fee is owed (no proration, DEC-017) minus the recorded payments
/// for that enrollment and period. Overpayment of a period is not carried to another (DEC-183). Dues are
/// never stored (DEC-021). Pure: callers pass the data in.
/// </summary>
public static class DueCalculator
{
    public static bool WasActiveIn(DateOnly startDate, DateOnly? endDate, YearMonth period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return startDate <= period.LastDay && (endDate is not { } end || end >= period.FirstDay);
    }

    /// <summary>Periods with at least one active day, from the start month up to <paramref name="upTo"/>.</summary>
    public static IEnumerable<YearMonth> ActivePeriods(DateOnly startDate, DateOnly? endDate, YearMonth upTo)
    {
        ArgumentNullException.ThrowIfNull(upTo);
        var last = endDate is { } end && YearMonth.From(end) < upTo ? YearMonth.From(end) : upTo;
        for (var period = YearMonth.From(startDate); period <= last; period = period.Next())
        {
            yield return period;
        }
    }

    /// <summary>
    /// Unpaid dues (remaining &gt; 0) from <paramref name="fromPeriod"/> (or the enrollment's start) up to
    /// <paramref name="currentPeriod"/>, ordered by period. <paramref name="recordedPayments"/> sums Recorded
    /// payments per enrollment and period.
    /// </summary>
    public static IReadOnlyList<Due> Outstanding(
        IEnumerable<DueEnrollment> enrollments,
        IReadOnlyDictionary<GroupId, IReadOnlyList<FeeRate>> fees,
        IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal> recordedPayments,
        YearMonth currentPeriod,
        YearMonth? fromPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(enrollments);
        ArgumentNullException.ThrowIfNull(fees);
        ArgumentNullException.ThrowIfNull(recordedPayments);

        var dues = new List<Due>();
        foreach (var (enrollment, period, fee) in Charges(enrollments, fees, currentPeriod, fromPeriod))
        {
            // Totals are plain decimals: a large overpayment can never overflow Money and break the report.
            var paid = recordedPayments.GetValueOrDefault((enrollment.Id, period));
            if (paid < fee.Amount)
            {
                dues.Add(new Due(enrollment.Id, enrollment.GroupId, period, fee, Money.Try(paid), Money.Try(fee.Amount - paid)));
            }
        }

        return [.. dues.OrderBy(d => d.Period)];
    }

    /// <summary>
    /// The full fee owed per enrollment and period before any payment: every period with at least one active
    /// day from <paramref name="fromPeriod"/> (or the start) up to <paramref name="currentPeriod"/> that has a
    /// fee. <see cref="Outstanding"/> subtracts the payments from these; the dashboard sums them as "due".
    /// </summary>
    public static IEnumerable<(DueEnrollment Enrollment, YearMonth Period, Money Fee)> Charges(
        IEnumerable<DueEnrollment> enrollments,
        IReadOnlyDictionary<GroupId, IReadOnlyList<FeeRate>> fees,
        YearMonth currentPeriod,
        YearMonth? fromPeriod = null)
    {
        // Validated eagerly; the iterator below only runs when enumerated.
        ArgumentNullException.ThrowIfNull(enrollments);
        ArgumentNullException.ThrowIfNull(fees);
        ArgumentNullException.ThrowIfNull(currentPeriod);
        return EnumerateCharges(enrollments, fees, currentPeriod, fromPeriod);
    }

    private static IEnumerable<(DueEnrollment Enrollment, YearMonth Period, Money Fee)> EnumerateCharges(
        IEnumerable<DueEnrollment> enrollments,
        IReadOnlyDictionary<GroupId, IReadOnlyList<FeeRate>> fees,
        YearMonth currentPeriod,
        YearMonth? fromPeriod)
    {
        foreach (var enrollment in enrollments)
        {
            if (!fees.TryGetValue(enrollment.GroupId, out var rates))
            {
                continue;
            }

            foreach (var period in ActivePeriods(enrollment.StartDate, enrollment.EndDate, currentPeriod))
            {
                if ((fromPeriod is null || period >= fromPeriod) && FeeRate.For(rates, period) is { } fee)
                {
                    yield return (enrollment, period, fee);
                }
            }
        }
    }
}
