using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

internal sealed class DuesReminderConfiguration : IEntityTypeConfiguration<DuesReminder>
{
    public void Configure(EntityTypeBuilder<DuesReminder> builder)
    {
        builder.ToTable("dues_reminders", Schemas.Billing, t =>
        {
            t.HasCheckConstraint("ck_dues_reminders_sent", "(status = 'Sent') = (sent_at IS NOT NULL) AND (status = 'Sent') = (recipient_count > 0)");
            t.HasCheckConstraint("ck_dues_reminders_skipped", "(status = 'Skipped') = (skip_reason IS NOT NULL)");
        });
        builder.HasKey(r => r.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3: the reminder's enrollment, student and group belong to its own school (as for payments, DEC-185).
        builder.HasOne<Enrollment>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.EnrollmentId, r.StudentId, r.GroupId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id, e.StudentId, e.GroupId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(r => r.Fee, MoneyMapping.Configure);

        // DEC-355: the idempotency key of the daily job; a second run cannot record the same reminder.
        builder.HasIndex(r => new { r.TenantId, r.EnrollmentId, r.Period, r.Kind }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.StudentId });
        builder.Ignore(r => r.DomainEvents);
    }
}

internal sealed class DuesReminderRepository(AppDbContext db) : IDuesReminderRepository
{
    public async Task<IReadOnlySet<(EnrollmentId Enrollment, YearMonth Period, DuesReminderKind Kind)>> ExistingAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        YearMonth firstPeriod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollmentIds);
        var ids = enrollmentIds.ToList();
        // At most two reminders per enrollment and period: few rows, so the period filter runs in memory.
        var rows = await db.Set<DuesReminder>().AsNoTracking()
            .Where(r => ids.Contains(r.EnrollmentId))
            .Select(r => new { r.EnrollmentId, r.Period, r.Kind })
            .ToListAsync(cancellationToken);
        return rows.Where(r => r.Period >= firstPeriod).Select(r => (r.EnrollmentId, r.Period, r.Kind)).ToHashSet();
    }

    public void Add(DuesReminder reminder) => db.Add(reminder);

    public Task<DuesReminder?> GetAsync(DuesReminderId id, CancellationToken cancellationToken) =>
        db.Set<DuesReminder>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
}
