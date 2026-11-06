using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments;

/// <summary>An enrollment for the School Admin (appendix D §2.8): history included, ended ones keep their dates.</summary>
public sealed record EnrollmentDto(
    Guid Id,
    Guid StudentId,
    string StudentFirstName,
    string StudentLastName,
    Guid GroupId,
    string GroupName,
    DateOnly StartDate,
    DateOnly? EndDate,
    EnrollmentStatus Status);

/// <summary>
/// One line of the coach's roster (US-14, DEC-007, NFR-PRV-02): name, birth year and the emergency
/// contact's phone, nothing else. A contract test pins this shape; do not add fields.
/// </summary>
public sealed record RosterEntryDto(string FirstName, string LastName, int BirthYear, string EmergencyContactPhone);

/// <summary>Enrollments of the current school (tenant filter applies).</summary>
public interface IEnrollmentRepository
{
    /// <summary>Tracked, for changes.</summary>
    Task<Enrollment?> GetAsync(EnrollmentId id, CancellationToken cancellationToken);

    void Add(Enrollment enrollment);

    Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken);

    /// <summary>Tracked active enrollments of the group, for ending them.</summary>
    Task<IReadOnlyList<Enrollment>> ListActiveOfGroupAsync(GroupId groupId, CancellationToken cancellationToken);

    /// <summary>Tracked active enrollments of the student, for ending them or checking the rules.</summary>
    Task<IReadOnlyList<Enrollment>> ListActiveOfStudentAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<Enrollment?> FindAsync(EnrollmentId id, CancellationToken cancellationToken);

    /// <summary>Newest start first.</summary>
    Task<PagedResult<Enrollment>> ListOfGroupAsync(GroupId groupId, EnrollmentStatus? status, PageRequest page, CancellationToken cancellationToken);

    /// <summary>The student's whole history, newest start first.</summary>
    Task<IReadOnlyList<Enrollment>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StudentId>> ActiveStudentsOfGroupsAsync(IReadOnlyCollection<GroupId> groupIds, CancellationToken cancellationToken);
}
