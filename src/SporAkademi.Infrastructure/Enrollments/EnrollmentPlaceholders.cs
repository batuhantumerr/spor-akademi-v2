using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Students;

namespace SporAkademi.Infrastructure.Enrollments;

/// <summary>Placeholder until the Enrollments module exists (P1-26): a student has no enrollments to end.</summary>
internal sealed class NoEnrollmentEnder : IEnrollmentEnder
{
    public Task EndActiveEnrollmentsOfStudentAsync(StudentId studentId, DateOnly endDate, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>Placeholder until the Enrollments module exists (P1-26): the group and sport filters do not restrict.</summary>
internal sealed class NoStudentGroupFilter : IStudentGroupFilter
{
    public Task<IReadOnlyCollection<StudentId>?> StudentsEnrolledInAsync(Guid? groupId, SportId? sportId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<StudentId>?>(null);
}
