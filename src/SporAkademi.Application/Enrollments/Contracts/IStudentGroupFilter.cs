using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments.Contracts;

/// <summary>
/// Read contract of the Enrollments module for the student search filters by group and sport (US-16).
/// Enrollments arrive in P1-26; until then the filters do not restrict the result.
/// </summary>
public interface IStudentGroupFilter
{
    /// <summary>
    /// Students actively enrolled in <paramref name="groupId"/> and/or in a group of <paramref name="sportId"/>;
    /// <c>null</c> means no restriction (neither filter given).
    /// </summary>
    Task<IReadOnlyCollection<StudentId>?> StudentsEnrolledInAsync(GroupId? groupId, SportId? sportId, CancellationToken cancellationToken);
}
