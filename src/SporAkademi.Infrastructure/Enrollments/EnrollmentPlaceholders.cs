using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Infrastructure.Enrollments;

/// <summary>
/// Stand-in until the Enrollments module exists: enrollments arrive with US-17 (P1-26) and are ended
/// with US-18 (P1-27). Until then no group has an enrollment, so nothing is counted or ended, and no
/// student is in any group or sport.
/// </summary>
internal sealed class NoEnrollments : IEnrollmentCounts, IEnrollmentEnder, IStudentGroupFilter
{
    public Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<Result> EndActiveEnrollmentsAsync(GroupId groupId, DateOnly endDate, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<Result> EndActiveEnrollmentsOfStudentAsync(StudentId studentId, DateOnly endDate, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    public Task<IReadOnlyCollection<StudentId>?> StudentsEnrolledInAsync(GroupId? groupId, SportId? sportId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<StudentId>?>(Array.Empty<StudentId>());
}
