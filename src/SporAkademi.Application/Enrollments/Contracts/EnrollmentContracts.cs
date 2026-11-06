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
