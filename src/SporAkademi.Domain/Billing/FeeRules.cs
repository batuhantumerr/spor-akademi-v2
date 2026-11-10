using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Domain.Billing;

/// <summary>Why a charge differs from the group's fee (P2-F8).</summary>
public enum ChargeAdjustment
{
    /// <summary>The group's fee in full.</summary>
    None,

    /// <summary>A sibling discount (DEC-371): the student is not the family's first-ranked child that month.</summary>
    SiblingDiscount,

    /// <summary>
    /// Not charged: the month is already charged by the student's enrollment that ended in it before this one started
    /// (a group change, DEC-372).
    /// </summary>
    GroupChange,
}

/// <summary>
/// A group's full fee for one enrollment and period, with the enrollment's student (input of <see cref="FeeRules"/>).
/// <see cref="Frozen"/> marks a charge of a closed period whose amounts are kept from its snapshot (DEC-375): its student
/// keeps a rank ahead of every student without a frozen charge that period.
/// </summary>
public sealed record StudentCharge(DueEnrollment Enrollment, StudentId StudentId, YearMonth Period, Money ListFee, bool Frozen = false);

/// <summary>
/// What an enrollment owes for a period under the fee rules: <see cref="ListFee"/> is the group's fee, <see cref="Fee"/>
/// what is owed, <see cref="Discount"/> the difference and <see cref="Adjustment"/> why.
/// </summary>
public sealed record EffectiveCharge(
    DueEnrollment Enrollment,
    StudentId StudentId,
    YearMonth Period,
    Money ListFee,
    Money Fee,
    ChargeAdjustment Adjustment,
    int DiscountPercent,
    int? SiblingRank)
{
    public Money Discount => ListFee.Subtract(Fee);
}

/// <summary>An effective charge not fully paid: what was paid and what remains.</summary>
public sealed record ChargedDue(EffectiveCharge Charge, Money Paid, Money Remaining);

/// <summary>
/// Applies a school's fee rules versions (P2-F8, DEC-370 – DEC-373) to the raw charges of <see cref="DueCalculator.Charges"/>.
/// Pure: callers pass the charges, each student's family and the versions in. Per period, the version in force is the
/// latest one not after it; without one every charge is the group's fee unchanged, exactly as before the rules.
/// <list type="number">
/// <item>Group change (when the version enables it): a student's enrollment that starts on or after the end date of
/// another of the student's enrollments ending in the same period is not charged that period, provided the earlier one
/// has a fee above zero there. Overlapping enrollments are both charged.</item>
/// <item>Sibling discount: within a family (students linked through a common guardian), the students with charges above
/// zero in the period are ranked by their total charge descending, then the earliest start of those enrollments, then
/// the student id. In a closed period, students with a frozen charge come first (in that order) and a student added
/// later only takes the next rank after them, so a late entry never makes a second child pay in full (DEC-375). Rank 1 pays in full; rank 2 gets <see cref="BillingRuleTerms.SecondChildPercent"/> off each of their
/// charges that period, rank 3 and later <see cref="BillingRuleTerms.ThirdAndLaterChildPercent"/>.</item>
/// </list>
/// Discounted fees are rounded to kuruş, half away from zero.
/// </summary>
public static class FeeRules
{
    /// <summary>
    /// The effective charges, in the order of <paramref name="charges"/>. <paramref name="familyOf"/> maps a student
    /// with siblings to a key shared by the whole family (any stable value); a student missing from it has no siblings.
    /// </summary>
    public static IReadOnlyList<EffectiveCharge> Apply(
        IEnumerable<StudentCharge> charges,
        IReadOnlyDictionary<StudentId, StudentId> familyOf,
        IReadOnlyCollection<BillingRuleTerms> versions)
    {
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(familyOf);
        ArgumentNullException.ThrowIfNull(versions);

        var input = charges.ToList();
        var result = new EffectiveCharge[input.Count];
        foreach (var period in input.Select((charge, index) => (Charge: charge, Index: index)).GroupBy(c => c.Charge.Period))
        {
            var terms = BillingRuleTerms.For(versions, period.Key);
            if (terms is null)
            {
                foreach (var (charge, index) in period)
                {
                    result[index] = Unchanged(charge);
                }

                continue;
            }

            var waived = terms.SingleChargeOnGroupChange ? WaivedByGroupChange(period.Key, [.. period]) : [];
            var rankOf = SiblingRanks([.. period.Where(c => !waived.Contains(c.Index)).Select(c => c.Charge)], familyOf);
            foreach (var (charge, index) in period)
            {
                result[index] = waived.Contains(index)
                    ? new EffectiveCharge(charge.Enrollment, charge.StudentId, charge.Period, charge.ListFee, Money.Zero, ChargeAdjustment.GroupChange, 0, null)
                    : Ranked(charge, rankOf.GetValueOrDefault(charge.StudentId), terms);
            }
        }

        return result;
    }

    /// <summary>
    /// Effective charges not fully paid (remaining &gt; 0), ordered by period; <paramref name="recordedPayments"/> sums the
    /// Recorded payments per enrollment and period. Like <see cref="DueCalculator.Outstanding"/>, an overpayment is never
    /// carried to another period (DEC-183) and a charge owing nothing (fully discounted or not charged) is never listed.
    /// </summary>
    public static IReadOnlyList<ChargedDue> Outstanding(
        IEnumerable<EffectiveCharge> charges,
        IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal> recordedPayments)
    {
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(recordedPayments);

        var dues = new List<ChargedDue>();
        foreach (var charge in charges)
        {
            // Totals are plain decimals: a large overpayment can never overflow Money and break the report.
            var paid = recordedPayments.GetValueOrDefault((charge.Enrollment.Id, charge.Period));
            if (paid < charge.Fee.Amount)
            {
                dues.Add(new ChargedDue(charge, Money.Try(paid), Money.Try(charge.Fee.Amount - paid)));
            }
        }

        return [.. dues.OrderBy(d => d.Charge.Period)];
    }

    /// <summary><paramref name="listFee"/> less <paramref name="percent"/> percent, rounded to kuruş half away from zero.</summary>
    public static Money Discounted(Money listFee, int percent)
    {
        ArgumentNullException.ThrowIfNull(listFee);
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, BillingRules.MinPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, BillingRules.MaxPercent);
        return Money.Try(decimal.Round(listFee.Amount * (100 - percent) / 100m, 2, MidpointRounding.AwayFromZero));
    }

    private static EffectiveCharge Unchanged(StudentCharge charge) =>
        new(charge.Enrollment, charge.StudentId, charge.Period, charge.ListFee, charge.ListFee, ChargeAdjustment.None, 0, null);

    private static EffectiveCharge Ranked(StudentCharge charge, int rank, BillingRuleTerms terms)
    {
        var percent = terms.PercentForRank(rank);
        var siblingRank = rank == 0 ? (int?)null : rank;
        return percent == 0
            ? new EffectiveCharge(charge.Enrollment, charge.StudentId, charge.Period, charge.ListFee, charge.ListFee, ChargeAdjustment.None, 0, siblingRank)
            : new EffectiveCharge(charge.Enrollment, charge.StudentId, charge.Period, charge.ListFee, Discounted(charge.ListFee, percent), ChargeAdjustment.SiblingDiscount, percent, siblingRank);
    }

    /// <summary>Indexes of the charges of <paramref name="period"/> that a group change leaves uncharged.</summary>
    private static HashSet<int> WaivedByGroupChange(YearMonth period, List<(StudentCharge Charge, int Index)> charges)
    {
        var waived = new HashSet<int>();
        foreach (var student in charges.GroupBy(c => c.Charge.StudentId))
        {
            // A fixed order, so of two one-day enrollments on the same day only the later one is waived.
            var ordered = student
                .OrderBy(c => c.Charge.Enrollment.StartDate)
                .ThenBy(c => c.Charge.Enrollment.EndDate ?? DateOnly.MaxValue)
                .ThenBy(c => c.Charge.Enrollment.Id.Value)
                .ToList();
            for (var later = 1; later < ordered.Count; later++)
            {
                var next = ordered[later].Charge.Enrollment;
                for (var earlier = 0; earlier < later; earlier++)
                {
                    var previous = ordered[earlier].Charge;
                    if (previous.ListFee.Amount > 0
                        && previous.Enrollment.EndDate is { } end
                        && YearMonth.From(end) == period
                        && end <= next.StartDate)
                    {
                        waived.Add(ordered[later].Index);
                        break;
                    }
                }
            }
        }

        return waived;
    }

    /// <summary>
    /// The rank of each student with siblings (1 = pays in full) among the family's students that are charged above zero
    /// in the period. Students without siblings, or charged nothing, are absent (no discount).
    /// </summary>
    private static Dictionary<StudentId, int> SiblingRanks(List<StudentCharge> charges, IReadOnlyDictionary<StudentId, StudentId> familyOf)
    {
        var ranks = new Dictionary<StudentId, int>();
        var payers = charges
            .GroupBy(c => c.StudentId)
            .Select(s => (Student: s.Key, Total: s.Sum(c => c.ListFee.Amount), FirstStart: s.Min(c => c.Enrollment.StartDate), Frozen: s.Any(c => c.Frozen)))
            .Where(p => p.Total > 0 && familyOf.ContainsKey(p.Student));
        foreach (var family in payers.GroupBy(p => familyOf[p.Student]))
        {
            var rank = 0;
            foreach (var payer in family.OrderByDescending(p => p.Frozen).ThenByDescending(p => p.Total).ThenBy(p => p.FirstStart).ThenBy(p => p.Student.Value))
            {
                ranks[payer.Student] = ++rank;
            }
        }

        return ranks;
    }
}
