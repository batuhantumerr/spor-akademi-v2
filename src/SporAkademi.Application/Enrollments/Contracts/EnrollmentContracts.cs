using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments.Contracts;

/// <summary>Read contract of the Enrollments module: how full a group is (US-11 capacity rule).</summary>
public interface IEnrollmentCounts
{
    Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken);
}

/// <summary>
/// A student's membership period in a group. The student counts as enrolled on every day from
/// <see cref="StartDate"/> to <see cref="EndDate"/>, both inclusive (DEC-170).
/// </summary>
public sealed record EnrollmentPeriod(StudentId StudentId, GroupId GroupId, DateOnly StartDate, DateOnly? EndDate)
{
    public bool Covers(DateOnly date) => StartDate <= date && (EndDate is not { } end || date <= end);

    public bool Overlaps(DateOnly firstDay, DateOnly lastDay) => StartDate <= lastDay && (EndDate is not { } end || end >= firstDay);
}

/// <summary>Read contract of the Enrollments module: who was enrolled when (attendance, US-19, US-21).</summary>
public interface IEnrollmentHistory
{
    /// <summary>Enrollments of the group that overlap the dates, both inclusive.</summary>
    Task<IReadOnlyList<EnrollmentPeriod>> ListOfGroupAsync(GroupId groupId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>Every enrollment of the student, in any group.</summary>
    Task<IReadOnlyList<EnrollmentPeriod>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken);
}

/// <summary>
/// Write contract of the Enrollments module, called inside the transaction of the triggering change (ADR-0009):
/// ends active enrollments on the given date.
/// </summary>
public interface IEnrollmentEnder
{
    /// <summary>The group's active enrollments (US-11, <see cref="GroupArchived"/>).</summary>
    Task<Result> EndActiveEnrollmentsAsync(GroupId groupId, DateOnly endDate, CancellationToken cancellationToken);

    /// <summary>The student's active enrollments in every group (US-15, <see cref="StudentLeft"/>).</summary>
    Task<Result> EndActiveEnrollmentsOfStudentAsync(StudentId studentId, DateOnly endDate, CancellationToken cancellationToken);
}

/// <summary>
/// The only way to take enrollment locks (ADR-0005, DEC-049): <c>SELECT ... FOR UPDATE</c> on the rows,
/// always the student before the group so two transactions never wait on each other in a cycle. Must be
/// called inside the unit of work's transaction; waits end after <c>lock_timeout</c> (5 s) with
/// <c>Lock.Timeout</c>. Unknown or other schools' rows are simply not locked.
/// </summary>
public interface IEnrollmentLocks
{
    /// <summary>For enrolling: the student, then the group.</summary>
    Task LockStudentAndGroupAsync(StudentId studentId, GroupId groupId, CancellationToken cancellationToken);

    /// <summary>For changes that depend on the group's active enrollments, e.g. lowering the capacity.</summary>
    Task LockGroupAsync(GroupId groupId, CancellationToken cancellationToken);
}
