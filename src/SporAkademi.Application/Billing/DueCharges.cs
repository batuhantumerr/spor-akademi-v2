using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>The effective charges of a window and the enrollments they belong to.</summary>
internal sealed record ChargeBook(IReadOnlyList<EffectiveCharge> Charges, IReadOnlyDictionary<EnrollmentId, EnrollmentInfo> Enrollments);

/// <summary>
/// The single source of what an enrollment owes for a period (P2-F8, DEC-373). The dues list, the dashboard, online
/// payments (amount and overpayment), manual payments and dues reminders all ask this service, so they can never
/// disagree. It reads the raw charges (<see cref="DueCalculator.Charges"/>: full fee per active month, no proration) and
/// applies the school's fee rules versions with <see cref="FeeRules"/>. Without a version in force the charges are the
/// group fees unchanged, and the sibling sets are not even read.
/// <para>
/// A fixed number of queries per call: the rules versions, the enrollments, their groups' fees and (only when a version
/// applies) the school's sibling sets. Ranking siblings needs every sibling's enrollments of the period, in any group,
/// so a school window always loads all of the school's enrollments of the window; callers filter the result.
/// </para>
/// </summary>
internal sealed class DueCharges(
    IEnrollmentDirectory enrollments,
    IGroupFeeSchedule fees,
    IBillingRulesRepository rules,
    IStudentSiblings siblings)
{
    /// <summary>Every effective charge of the school from <paramref name="from"/> to <paramref name="to"/> (inclusive).</summary>
    public async Task<ChargeBook> ForSchoolAsync(YearMonth from, YearMonth to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var versions = await VersionsAsync(cancellationToken);
        var active = await enrollments.ListActiveBetweenAsync(null, from.FirstDay, to.LastDay, cancellationToken);
        var familyOf = versions.Any(v => v.EffectiveFrom <= to)
            ? await FamiliesAsync([.. active.Select(e => e.StudentId).Distinct()], cancellationToken)
            : new Dictionary<StudentId, StudentId>();
        return await ChargeAsync(active, from, to, versions, familyOf, cancellationToken);
    }

    /// <summary>
    /// The effective charge of one enrollment in one period, or null when it owes nothing there because it was not active
    /// or its group has no fee for the period. Ranks it among its siblings' charges of that period exactly as
    /// <see cref="ForSchoolAsync"/> does.
    /// </summary>
    public async Task<EffectiveCharge?> ForEnrollmentAsync(EnrollmentInfo enrollment, YearMonth period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(period);

        var versions = await VersionsAsync(cancellationToken);
        IReadOnlyList<EnrollmentInfo> related = [enrollment];
        var familyOf = new Dictionary<StudentId, StudentId>();
        if (BillingRuleTerms.For(versions, period) is not null)
        {
            // The student's own other enrollments (group change) and every sibling's enrollments (ranking), in one query.
            var family = (await siblings.GetSiblingSetsAsync([enrollment.StudentId], cancellationToken)).GetValueOrDefault(enrollment.StudentId);
            foreach (var student in family ?? [])
            {
                familyOf[student] = family![0];
            }

            related = await enrollments.ListOfStudentsActiveBetweenAsync(family ?? [enrollment.StudentId], period.FirstDay, period.LastDay, cancellationToken);
        }

        var book = await ChargeAsync(related, period, period, versions, familyOf, cancellationToken);
        return book.Charges.FirstOrDefault(c => c.Enrollment.Id == enrollment.Id);
    }

    private async Task<IReadOnlyList<BillingRuleTerms>> VersionsAsync(CancellationToken cancellationToken) =>
        [.. (await rules.ListAsync(cancellationToken)).Select(r => r.Terms)];

    private async Task<Dictionary<StudentId, StudentId>> FamiliesAsync(IReadOnlyCollection<StudentId> students, CancellationToken cancellationToken)
    {
        var sets = await siblings.GetSiblingSetsAsync(students, cancellationToken);
        return sets.ToDictionary(s => s.Key, s => s.Value[0]);
    }

    private async Task<ChargeBook> ChargeAsync(
        IReadOnlyList<EnrollmentInfo> active,
        YearMonth from,
        YearMonth to,
        IReadOnlyList<BillingRuleTerms> versions,
        IReadOnlyDictionary<StudentId, StudentId> familyOf,
        CancellationToken cancellationToken)
    {
        var groupFees = await fees.GetFeesAsync([.. active.Select(e => e.GroupId).Distinct()], cancellationToken);
        var rates = groupFees.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<FeeRate>)[.. g.Value.Select(f => new FeeRate(f.EffectiveFrom, f.Amount))]);
        var byId = active.ToDictionary(e => e.Id);

        var raw = DueCalculator.Charges(
                active.Select(e => new DueEnrollment(e.Id, e.GroupId, e.StartDate, e.EndDate)),
                rates,
                to,
                from)
            .Select(c => new StudentCharge(c.Enrollment, byId[c.Enrollment.Id].StudentId, c.Period, c.Fee));
        return new ChargeBook(FeeRules.Apply(raw, familyOf, versions), byId);
    }
}
