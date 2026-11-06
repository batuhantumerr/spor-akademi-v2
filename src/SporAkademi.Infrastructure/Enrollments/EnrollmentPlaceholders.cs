using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Enrollments;

/// <summary>
/// Stand-in until the Enrollments module exists: enrollments arrive with US-17 (P1-26) and are ended
/// with US-18 (P1-27). Until then no group has an enrollment, so nothing is counted or ended.
/// </summary>
internal sealed class NoEnrollments : IEnrollmentCounts, IEnrollmentEnder
{
    public Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<Result> EndActiveEnrollmentsAsync(GroupId groupId, DateOnly endDate, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
