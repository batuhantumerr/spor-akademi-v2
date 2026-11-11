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
/// Closed periods (DEC-375) are frozen: an enrollment with a <see cref="ChargeSnapshot"/> for a period owes exactly the
/// snapshot, whatever happened to guardian links since. An enrollment without one in a closed period (enrolled
/// retroactively after the close) is computed live and only takes the next rank after its frozen siblings (it never
/// pays in full ahead of a frozen sibling), and never changes their amounts. Only open months (the current one and later) follow today's links and rules.
/// </para>
/// <para>
/// A fixed number of queries per call: the rules versions, the enrollments, their groups' fees and, only when a version
/// applies, the sibling sets and the snapshots. A school window reads all of the school's enrollments of the window
/// (siblings rank across groups; callers filter the result) and all sibling links at once; a single due reads only the
/// student's family (DEC-373).
/// </para>
/// </summary>
internal sealed class DueCharges(
    IEnrollmentDirectory enrollments,
    IGroupFeeSchedule fees,
    IBillingRulesRepository rules,
    IStudentSiblings siblings,
    IChargeSnapshotRepository snapshots)
{
    /// <summary>Every effective charge of the school from <paramref name="from"/> to <paramref name="to"/> (inclusive).</summary>
    public async Task<ChargeBook> ForSchoolAsync(YearMonth from, YearMonth to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var versions = await VersionsAsync(cancellationToken);
        var active = await enrollments.ListActiveBetweenAsync(null, from.FirstDay, to.LastDay, cancellationToken);
        if (!versions.Any(v => v.EffectiveFrom <= to))
        {
            return await ChargeAsync(active, from, to, versions, new Dictionary<StudentId, StudentId>(), [], cancellationToken);
        }

        var familyOf = await FamiliesAsync([.. active.Select(e => e.StudentId).Distinct()], cancellationToken);
        var frozen = await snapshots.ListAsync(from, to, cancellationToken);
        return await ChargeAsync(active, from, to, versions, familyOf, frozen, cancellationToken);
    }

    /// <summary>
    /// The effective charges of one student's family (the student and their siblings) from <paramref name="from"/> to
    /// <paramref name="to"/>: the same amounts <see cref="ForSchoolAsync"/> gives them, because siblings only rank among
    /// themselves, but reading only the family's enrollments (P3-03, DEC-388: the parent's dues).
    /// </summary>
    public async Task<ChargeBook> ForFamilyAsync(StudentId studentId, YearMonth from, YearMonth to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var versions = await VersionsAsync(cancellationToken);
        if (!versions.Any(v => v.EffectiveFrom <= to))
        {
            var own = await enrollments.ListOfStudentsActiveBetweenAsync([studentId], from.FirstDay, to.LastDay, cancellationToken);
            return await ChargeAsync(own, from, to, versions, new Dictionary<StudentId, StudentId>(), [], cancellationToken);
        }

        var family = await siblings.GetFamilyAsync(studentId, cancellationToken);
        var familyOf = new Dictionary<StudentId, StudentId>();
        if (family.Count > 1)
        {
            foreach (var student in family)
            {
                familyOf[student] = family[0];
            }
        }

        var related = await enrollments.ListOfStudentsActiveBetweenAsync(family, from.FirstDay, to.LastDay, cancellationToken);
        var frozen = await snapshots.ListOfStudentsBetweenAsync(family, from, to, cancellationToken);
        return await ChargeAsync(related, from, to, versions, familyOf, frozen, cancellationToken);
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
        IReadOnlyList<ChargeSnapshot> frozen = [];
        if (BillingRuleTerms.For(versions, period) is not null)
        {
            // Only this student's family is read; then the student's own other enrollments (group change), every
            // sibling's enrollments (ranking) and their frozen charges of a closed period, one query each.
            var family = await siblings.GetFamilyAsync(enrollment.StudentId, cancellationToken);
            if (family.Count > 1)
            {
                foreach (var student in family)
                {
                    familyOf[student] = family[0];
                }
            }

            related = await enrollments.ListOfStudentsActiveBetweenAsync(family, period.FirstDay, period.LastDay, cancellationToken);
            frozen = await snapshots.ListOfStudentsAsync(family, period, cancellationToken);
        }

        var book = await ChargeAsync(related, period, period, versions, familyOf, frozen, cancellationToken);
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
        IReadOnlyList<ChargeSnapshot> frozen,
        CancellationToken cancellationToken)
    {
        var groupFees = await fees.GetFeesAsync([.. active.Select(e => e.GroupId).Distinct()], cancellationToken);
        var rates = groupFees.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<FeeRate>)[.. g.Value.Select(f => new FeeRate(f.EffectiveFrom, f.Amount))]);
        var byId = active.ToDictionary(e => e.Id);
        var snapshotOf = frozen.ToDictionary(f => (f.EnrollmentId, f.Period));

        // A frozen charge ranks with its frozen list fee and keeps its frozen amounts; charges without a snapshot are live.
        var raw = DueCalculator.Charges(
                active.Select(e => new DueEnrollment(e.Id, e.GroupId, e.StartDate, e.EndDate)),
                rates,
                to,
                from)
            .Select(c => new StudentCharge(
                c.Enrollment,
                byId[c.Enrollment.Id].StudentId,
                c.Period,
                snapshotOf.TryGetValue((c.Enrollment.Id, c.Period), out var snapshot) ? snapshot.ListFee : c.Fee,
                snapshot is not null));
        var charges = FeeRules.Apply(raw, familyOf, versions);
        return snapshotOf.Count == 0
            ? new ChargeBook(charges, byId)
            : new ChargeBook(
                [.. charges.Select(c => snapshotOf.TryGetValue((c.Enrollment.Id, c.Period), out var snapshot) ? snapshot.ToCharge(c.Enrollment) : c)],
                byId);
    }
}
