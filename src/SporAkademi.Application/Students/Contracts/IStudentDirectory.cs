using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students.Contracts;

/// <summary>
/// A student of the current school as other modules see it: only what enrollment rules and the coach
/// roster need (DEC-007). Guardians are deliberately absent. Birth date and emergency phone are null only for an
/// anonymized student (DEC-363), whose name is then the "Anonim Öğrenci #…" placeholder.
/// </summary>
public sealed record StudentInfo(
    Guid Id,
    string FirstName,
    string LastName,
    DateOnly? BirthDate,
    StudentStatus Status,
    string? EmergencyContactPhone);

/// <summary>Read contract of the Students module (used by Enrollments).</summary>
public interface IStudentDirectory
{
    Task<StudentInfo?> FindAsync(StudentId studentId, CancellationToken cancellationToken);

    /// <summary>Students of the current school among the given ids; unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, StudentInfo>> GetManyAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken);
}
