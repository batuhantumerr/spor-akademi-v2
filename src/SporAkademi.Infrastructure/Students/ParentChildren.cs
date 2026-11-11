using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

/// <summary>
/// DEC-382: the signed-in parent's children, in one query: guardian (by its account) → links → active students, and
/// only while the account itself is active (users are not tenant-filtered, so its school is stated). The tenant filter
/// applies to guardians, links and students, so another school's rows never match either.
/// </summary>
internal sealed class ParentChildren(AppDbContext db, ICurrentUser currentUser) : IParentChildren
{
    public async Task<IReadOnlyList<ParentChild>> ListAsync(CancellationToken cancellationToken)
    {
        if (Children() is not { } children)
        {
            return [];
        }

        var rows = await children
            .OrderBy(c => c.Student.FirstName).ThenBy(c => c.Student.LastName).ThenBy(c => c.Student.Id)
            .Select(c => new { c.Student.Id, c.Student.FirstName, c.Student.LastName, c.Student.BirthDate, c.Student.Gender, c.Relationship })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ParentChild(r.Id.Value, r.FirstName, r.LastName, r.BirthDate, r.Gender, r.Relationship))];
    }

    public async Task<bool> IsMineAsync(StudentId studentId, CancellationToken cancellationToken) =>
        Children() is { } children && await children.AnyAsync(c => c.Student.Id == studentId, cancellationToken);

    private IQueryable<Child>? Children()
    {
        if (currentUser is not { Role: Role.Parent, UserId: { } userId, TenantId: { } tenant })
        {
            return null;
        }

        var account = UserId.From(userId);
        var school = Domain.Tenancy.SchoolId.From(tenant);
        return from guardian in db.Set<Guardian>().AsNoTracking()
               join link in db.Set<StudentGuardian>().AsNoTracking() on guardian.Id equals link.GuardianId
               join student in db.Set<Student>().AsNoTracking() on link.StudentId equals student.Id
               where guardian.AccountUserId == userId
                     && student.Status == StudentStatus.Active
                     && student.AnonymizedAt == null
                     && db.Set<User>().Any(u => u.Id == account && u.TenantId == school && u.Role == UserRole.Parent && u.Status == UserStatus.Active
                         // Not a re-invited account still waiting for its new password: a token issued before the
                         // revocation must not see the children again (DEC-384).
                         && !u.MustChangePassword)
               select new Child { Student = student, Relationship = link.Relationship };
    }

    /// <summary>A member-init projection, so EF Core can keep composing (order, filter) on it.</summary>
    private sealed class Child
    {
        public required Student Student { get; init; }

        public required GuardianRelationship Relationship { get; init; }
    }
}
