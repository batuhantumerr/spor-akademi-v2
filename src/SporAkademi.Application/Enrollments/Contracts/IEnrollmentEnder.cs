using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments.Contracts;

/// <summary>
/// Write contract of the Enrollments module, used when a student leaves (US-15, appendix B §6).
/// Enrollments arrive in P1-26; until then there is nothing to end.
/// </summary>
public interface IEnrollmentEnder
{
    Task EndActiveEnrollmentsOfStudentAsync(StudentId studentId, DateOnly endDate, CancellationToken cancellationToken);
}
