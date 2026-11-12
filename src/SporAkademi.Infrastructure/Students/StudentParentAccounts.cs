using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

/// <summary>
/// P3-08: <see cref="ParentChildren"/> read the other way round, in one query (guardian links → active students → the
/// guardian's active parent account of the same school, not waiting for a new password). The tenant filter applies to
/// guardians, links and students; users are not tenant-filtered, so their school is stated.
/// </summary>
internal sealed class StudentParentAccounts(AppDbContext db, ICurrentUser currentUser) : IStudentParentAccounts
{
    public async Task<IReadOnlyList<StudentParentAccount>> ListAsync(IReadOnlyCollection<StudentId> studentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studentIds);
        if (studentIds.Count == 0 || currentUser.TenantId is not { } tenant)
        {
            return [];
        }

        var school = SchoolId.From(tenant);
        var ids = studentIds.Distinct().ToList();
        var links = await (
                from link in db.Set<StudentGuardian>().AsNoTracking()
                join student in db.Set<Student>().AsNoTracking() on link.StudentId equals student.Id
                join guardian in db.Set<Guardian>().AsNoTracking() on link.GuardianId equals guardian.Id
                where ids.Contains(student.Id)
                      && student.Status == StudentStatus.Active
                      && student.AnonymizedAt == null
                      && guardian.AccountUserId != null
                select new { student.Id, Account = guardian.AccountUserId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return [];
        }

        var accounts = links.Select(l => UserId.From(l.Account)).Distinct().ToList();
        var active = (await db.Set<User>().AsNoTracking()
                .Where(u => accounts.Contains(u.Id)
                    && u.TenantId == school
                    && u.Role == UserRole.Parent
                    && u.Status == UserStatus.Active
                    && !u.MustChangePassword)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken))
            .Select(id => id.Value)
            .ToHashSet();
        return [.. links.Where(l => active.Contains(l.Account)).Select(l => new StudentParentAccount(l.Id.Value, l.Account))];
    }
}
