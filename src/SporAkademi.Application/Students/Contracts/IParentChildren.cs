using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students.Contracts;

/// <summary>A child of the signed-in parent: what any parent view may name (DEC-384).</summary>
public sealed record ParentChild(Guid StudentId, string FirstName, string LastName, DateOnly? BirthDate, Gender Gender, GuardianRelationship Relationship);

/// <summary>
/// The second authorization boundary after the school (P3-01, DEC-381): which students the signed-in parent may see.
/// They are the active, not anonymized students linked to the guardian whose parent account the caller is, and only
/// while that account is active (a revoked account sees nothing at once, even before its access token expires). Every
/// parent endpoint asks this before it reads anything about a student; a student that is not the caller's is
/// "not found", like another school's (US-02). For any caller who is not a parent the answer is always empty.
/// </summary>
public interface IParentChildren
{
    /// <summary>The caller's children by first name, then last name.</summary>
    Task<IReadOnlyList<ParentChild>> ListAsync(CancellationToken cancellationToken);

    Task<bool> IsMineAsync(StudentId studentId, CancellationToken cancellationToken);
}
