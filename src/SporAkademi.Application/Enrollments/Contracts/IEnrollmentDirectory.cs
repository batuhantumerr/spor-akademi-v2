using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments.Contracts;

/// <summary>An enrollment of the current school as other modules see it.</summary>
public sealed record EnrollmentInfo(EnrollmentId Id, StudentId StudentId, GroupId GroupId, DateOnly StartDate, DateOnly? EndDate);

/// <summary>Read contract of the Enrollments module (used by Billing, US-23, US-24).</summary>
public interface IEnrollmentDirectory
{
    Task<EnrollmentInfo?> FindAsync(EnrollmentId enrollmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="FindAsync"/>, but first locks the enrollment row (<c>FOR NO KEY UPDATE</c>) until the
    /// transaction ends, so a concurrent change to the enrollment (e.g. ending it) or another locked caller waits and
    /// cannot invalidate a rule checked against it. Must be called inside the unit of work's transaction.
    /// </summary>
    Task<EnrollmentInfo?> FindLockedAsync(EnrollmentId enrollmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Enrollments (any status) that were active at least one day between <paramref name="firstDay"/> and
    /// <paramref name="lastDay"/> (both inclusive; no first day means from the beginning), optionally of one group only.
    /// </summary>
    Task<IReadOnlyList<EnrollmentInfo>> ListActiveBetweenAsync(GroupId? groupId, DateOnly? firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>
    /// Enrollments (any status, any group) of the given students that were active at least one day between
    /// <paramref name="firstDay"/> and <paramref name="lastDay"/> (both inclusive), in one query (fee rules, DEC-373).
    /// </summary>
    Task<IReadOnlyList<EnrollmentInfo>> ListOfStudentsActiveBetweenAsync(
        IReadOnlyCollection<StudentId> studentIds,
        DateOnly firstDay,
        DateOnly lastDay,
        CancellationToken cancellationToken);
}
