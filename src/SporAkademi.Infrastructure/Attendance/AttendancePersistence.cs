using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Attendance;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Attendance;

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    /// <summary>Appendix B 4.9: one session per group, date and start time; concurrent first saves meet here (DEC-173).</summary>
    public const string UniqueKeyIndex = "ix_sessions_group_date_start";

    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions", Schemas.Attendance, table => table.HasCheckConstraint("ck_sessions_time", "end_time > start_time"));
        builder.HasKey(s => s.Id);
        builder.HasAlternateKey(s => new { s.TenantId, s.Id }).HasName("ak_sessions_tenant_id_id");
        builder.HasOne<School>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Group>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.GroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.TenantId, s.GroupId, s.Date, s.StartTime }).IsUnique().HasDatabaseName(UniqueKeyIndex);

        builder.HasMany(s => s.Records).WithOne()
            .HasForeignKey(r => new { r.TenantId, r.SessionId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Records).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(s => s.Changes).WithOne()
            .HasForeignKey(c => new { c.TenantId, c.SessionId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Changes).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(s => s.Key);
        builder.Ignore(s => s.StartsAt);
        builder.Ignore(s => s.EndsAt);
        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    /// <summary>One record per student and session; a concurrent first record meets here (DEC-173).</summary>
    public const string PrimaryKey = "pk_attendance_records";

    public void Configure(EntityTypeBuilder<AttendanceRecord> builder)
    {
        builder.ToTable("attendance_records", Schemas.Attendance);
        builder.HasKey(r => new { r.SessionId, r.StudentId }).HasName(PrimaryKey);
        builder.HasOne<Student>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.StudentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AttendanceChangeConfiguration : IEntityTypeConfiguration<AttendanceChange>
{
    public void Configure(EntityTypeBuilder<AttendanceChange> builder)
    {
        builder.ToTable("attendance_changes", Schemas.Attendance);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => new { c.TenantId, c.StudentId });
    }
}

/// <summary>
/// Transaction-scoped advisory lock per session key (DEC-173), like the coach schedule locks (DEC-148).
/// The key is deterministic, so it works across API instances; a collision only serializes a little more.
/// </summary>
internal sealed class SessionLocks(AppDbContext db) : ISessionLocks
{
    /// <summary>Namespace of the session locks ("ATTN" as bytes).</summary>
    private const int SessionSpace = 0x4154544E;

    public async Task LockAsync(GroupId groupId, SessionKey key, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Session locks are only taken inside the unit of work's transaction.");
        }

        var lockKey = unchecked((Groups.GroupLocks.KeyOf(groupId.Value) * 31) + (key.Date.DayNumber * 1440) + (key.Start.Hour * 60) + key.Start.Minute);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SessionSpace}, {lockKey})", cancellationToken);
    }
}

internal sealed class SessionRepository(AppDbContext db) : ISessionRepository
{
    public Task<Session?> GetAsync(GroupId groupId, SessionKey key, CancellationToken cancellationToken) =>
        db.Set<Session>().Include(s => s.Records)
            .SingleOrDefaultAsync(s => s.GroupId == groupId && s.Date == key.Date && s.StartTime == key.Start, cancellationToken);

    public void Add(Session session) => db.Add(session);

    public void Touch(Session session)
    {
        var entry = db.Entry(session);
        if (entry.State == EntityState.Unchanged)
        {
            entry.State = EntityState.Modified;
        }
    }

    public Task<Session?> FindAsync(GroupId groupId, SessionKey key, CancellationToken cancellationToken) =>
        db.Set<Session>().AsNoTracking().Include(s => s.Records)
            .SingleOrDefaultAsync(s => s.GroupId == groupId && s.Date == key.Date && s.StartTime == key.Start, cancellationToken);

    public async Task<IReadOnlyList<Session>> ListOfGroupAsync(GroupId groupId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        await db.Set<Session>().AsNoTracking().Include(s => s.Records)
            .Where(s => s.GroupId == groupId && s.Date >= firstDay && s.Date <= lastDay)
            .OrderBy(s => s.Date).ThenBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StudentSessionRecord>> ListOfStudentAsync(StudentId studentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken) =>
        await (
                from record in db.Set<AttendanceRecord>().AsNoTracking()
                join session in db.Set<Session>().AsNoTracking() on record.SessionId equals session.Id
                where record.StudentId == studentId && session.Date >= firstDay && session.Date <= lastDay
                orderby session.Date, session.StartTime
                select new StudentSessionRecord(session.GroupId, session.Date, session.StartTime, session.EndTime, record.Status))
            .ToListAsync(cancellationToken);
}
