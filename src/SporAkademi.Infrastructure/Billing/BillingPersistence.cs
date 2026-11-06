using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", Schemas.Billing, t =>
        {
            t.HasCheckConstraint("ck_payments_amount", "amount > 0");
            // A cancellation always has its reason, author and time; a recorded payment has none of them.
            t.HasCheckConstraint(
                "ck_payments_cancellation",
                "(status = 'Cancelled') = (cancel_reason IS NOT NULL) AND (status = 'Cancelled') = (cancelled_at IS NOT NULL) AND (status = 'Cancelled') = (cancelled_by IS NOT NULL)");
        });
        builder.HasKey(p => p.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3: a payment only references an enrollment, student and group of its own school.
        builder.HasOne<Enrollment>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.EnrollmentId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Student>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.StudentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Group>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.GroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(p => p.Amount, MoneyMapping.Configure);
        builder.Property(p => p.CancelReason).HasMaxLength(Payment.CancelReasonMaxLength);

        builder.HasIndex(p => new { p.TenantId, p.EnrollmentId, p.Period });
        builder.HasIndex(p => new { p.TenantId, p.GroupId, p.Period });
        builder.HasIndex(p => new { p.TenantId, p.StudentId });
        builder.HasIndex(p => new { p.TenantId, p.PaidOn });
        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetAsync(PaymentId id, CancellationToken cancellationToken) =>
        db.Set<Payment>().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public void Add(Payment payment) => db.Add(payment);

    public Task<Payment?> FindAsync(PaymentId id, CancellationToken cancellationToken) =>
        db.Set<Payment>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<PagedResult<Payment>> ListAsync(PaymentFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var query = db.Set<Payment>().AsNoTracking();
        if (filter.StudentId is { } studentId)
        {
            query = query.Where(p => p.StudentId == studentId);
        }

        if (filter.GroupId is { } groupId)
        {
            query = query.Where(p => p.GroupId == groupId);
        }

        if (filter.Period is { } period)
        {
            query = query.Where(p => p.Period == period);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.Id)
            .Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<Payment>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), Money>> RecordedTotalsAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollmentIds);
        if (enrollmentIds.Count == 0)
        {
            return new Dictionary<(EnrollmentId, YearMonth), Money>();
        }

        var ids = enrollmentIds.Distinct().ToList();
        var recorded = await db.Set<Payment>().AsNoTracking()
            .Where(p => ids.Contains(p.EnrollmentId) && p.Status == PaymentStatus.Recorded)
            .ToListAsync(cancellationToken);

        // Summed with Money in memory: exact decimal arithmetic, no database rounding.
        return recorded
            .GroupBy(p => (p.EnrollmentId, p.Period))
            .ToDictionary(g => g.Key, g => g.Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount)));
    }
}
