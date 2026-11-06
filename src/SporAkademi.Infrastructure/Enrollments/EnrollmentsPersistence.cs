using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Enrollments;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Enrollments;

internal sealed class EnrollmentConfiguration : IEntityTypeConfiguration<Enrollment>
{
    /// <summary>DEC-050: at most one active enrollment of a student in a group; backs the policy check.</summary>
    public const string ActiveIndex = "ix_enrollments_active_student_group";

    public void Configure(EntityTypeBuilder<Enrollment> builder)
    {
        builder.ToTable("enrollments", Schemas.Enrollments, table => table.HasCheckConstraint(
            "ck_enrollments_end_date",
            "(status = 'Ended') = (end_date IS NOT NULL) AND (end_date IS NULL OR end_date >= start_date)"));
        builder.HasKey(e => e.Id);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id }).HasName("ak_enrollments_tenant_id_id");
        builder.HasOne<School>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3: an enrollment can only join a student and a group of its own school.
        builder.HasOne<Student>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.StudentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Group>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.GroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TenantId, e.StudentId, e.GroupId })
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName(ActiveIndex);
        builder.HasIndex(e => new { e.TenantId, e.GroupId, e.Status });
        builder.Ignore(e => e.DomainEvents);
    }
}

internal sealed class EnrollmentRepository(AppDbContext db) : IEnrollmentRepository
{
    public Task<Enrollment?> GetAsync(EnrollmentId id, CancellationToken cancellationToken) =>
        db.Set<Enrollment>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public void Add(Enrollment enrollment) => db.Add(enrollment);

    public Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken) =>
        db.Set<Enrollment>().CountAsync(e => e.GroupId == groupId && e.Status == EnrollmentStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<Enrollment>> ListActiveOfGroupAsync(GroupId groupId, CancellationToken cancellationToken) =>
        await db.Set<Enrollment>().Where(e => e.GroupId == groupId && e.Status == EnrollmentStatus.Active).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Enrollment>> ListActiveOfStudentAsync(StudentId studentId, CancellationToken cancellationToken) =>
        await db.Set<Enrollment>().Where(e => e.StudentId == studentId && e.Status == EnrollmentStatus.Active).ToListAsync(cancellationToken);

    public Task<Enrollment?> FindAsync(EnrollmentId id, CancellationToken cancellationToken) =>
        db.Set<Enrollment>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<PagedResult<Enrollment>> ListOfGroupAsync(GroupId groupId, EnrollmentStatus? status, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = db.Set<Enrollment>().AsNoTracking().Where(e => e.GroupId == groupId && (status == null || e.Status == status));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(e => e.StartDate).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<Enrollment>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<Enrollment>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken) =>
        await db.Set<Enrollment>().AsNoTracking().Where(e => e.StudentId == studentId)
            .OrderByDescending(e => e.StartDate).ThenByDescending(e => e.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Enrollment>> ListOfGroupBetweenAsync(GroupId groupId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        await db.Set<Enrollment>().AsNoTracking()
            .Where(e => e.GroupId == groupId && e.StartDate <= lastDay && (e.EndDate == null || e.EndDate >= firstDay))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StudentId>> ActiveStudentsOfGroupsAsync(IReadOnlyCollection<GroupId> groupIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        var ids = groupIds.ToList();
        return await db.Set<Enrollment>()
            .Where(e => ids.Contains(e.GroupId) && e.Status == EnrollmentStatus.Active)
            .Select(e => e.StudentId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
/// ADR-0005 row locks. Raw SQL bypasses the tenant filter, so the school is part of every condition
/// (section 5.4); without a school nothing is locked.
/// </summary>
internal sealed class EnrollmentLocks(AppDbContext db, ICurrentUser currentUser) : IEnrollmentLocks
{
    public async Task LockStudentAndGroupAsync(StudentId studentId, GroupId groupId, CancellationToken cancellationToken)
    {
        var tenant = RequireTransactionAndTenant();
        // Always the student first, then the group (DEC-049): a fixed order cannot deadlock.
        await db.Database.ExecuteSqlAsync(
            $"SELECT id FROM students.students WHERE tenant_id = {tenant} AND id = {studentId.Value} FOR UPDATE",
            cancellationToken);
        await LockGroupRowAsync(tenant, groupId, cancellationToken);
    }

    public Task LockGroupAsync(GroupId groupId, CancellationToken cancellationToken) =>
        LockGroupRowAsync(RequireTransactionAndTenant(), groupId, cancellationToken);

    private async Task LockGroupRowAsync(Guid tenant, GroupId groupId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT id FROM groups.groups WHERE tenant_id = {tenant} AND id = {groupId.Value} FOR UPDATE",
            cancellationToken);

    private Guid RequireTransactionAndTenant()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Enrollment locks are only taken inside the unit of work's transaction.");
        }

        return currentUser.TenantId ?? throw new InvalidOperationException("Enrollment locks need a school.");
    }
}
