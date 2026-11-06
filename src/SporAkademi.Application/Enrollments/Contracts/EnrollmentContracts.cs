using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Enrollments.Contracts;

/// <summary>Read contract of the Enrollments module: how full a group is (US-11 capacity rule).</summary>
public interface IEnrollmentCounts
{
    Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken);
}

/// <summary>
/// Write contract of the Enrollments module, called inside the archiving transaction (ADR-0009): ends
/// the group's active enrollments on the given date (US-11, <see cref="GroupArchived"/>).
/// </summary>
public interface IEnrollmentEnder
{
    Task<Result> EndActiveEnrollmentsAsync(GroupId groupId, DateOnly endDate, CancellationToken cancellationToken);
}
