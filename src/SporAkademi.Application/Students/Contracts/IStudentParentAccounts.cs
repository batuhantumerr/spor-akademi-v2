using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students.Contracts;

/// <summary>A parent account that may see the student (the same rule as <see cref="IParentChildren"/>).</summary>
public sealed record StudentParentAccount(Guid StudentId, Guid UserId);

/// <summary>
/// The reverse of <see cref="IParentChildren"/> for background work (P3-08): the parent accounts of the current school
/// that would see each of the given students right now. Only active, not anonymized students; only active parent
/// accounts of the same school that are not waiting for a new password (DEC-384). Unknown students are omitted.
/// </summary>
public interface IStudentParentAccounts
{
    Task<IReadOnlyList<StudentParentAccount>> ListAsync(IReadOnlyCollection<StudentId> studentIds, CancellationToken cancellationToken);
}
