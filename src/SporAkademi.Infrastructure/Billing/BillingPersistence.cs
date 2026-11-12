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
    public const string OnlinePaymentIndex = "ix_payments_online_payment_id";

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
        // DEC-185: one FK over the enrollment and the denormalized student and group, so they can never diverge.
        builder.HasOne<Enrollment>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.EnrollmentId, p.StudentId, p.GroupId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id, e.StudentId, e.GroupId })
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

        // DEC-342: an online payment records at most one payment, whatever races between webhook deliveries.
        builder.HasOne<OnlinePayment>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.OnlinePaymentId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.OnlinePaymentId).IsUnique().HasFilter("online_payment_id IS NOT NULL").HasDatabaseName(OnlinePaymentIndex);
        // Only the online flow sets it, and only for the Online method.
        builder.ToTable(t => t.HasCheckConstraint("ck_payments_online", "(method = 'Online') = (online_payment_id IS NOT NULL)"));
        // P3-13 (DEC-417): only an online payment's money is returned through the provider.
        builder.ToTable(t => t.HasCheckConstraint("ck_payments_refunded", "status <> 'Refunded' OR method = 'Online'"));
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

        if (filter.Statuses is { } statuses)
        {
            var allowed = statuses.Distinct().ToList();
            query = query.Where(p => allowed.Contains(p.Status));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.Id)
            .Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<Payment>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> RecordedTotalsAsync(
        GroupId? groupId,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firstPeriod);
        ArgumentNullException.ThrowIfNull(lastPeriod);

        var periods = PeriodsOf(firstPeriod, lastPeriod);

        // Grouped and summed in the database (numeric, exact); the indexes on (tenant, group, period) serve it.
        return await SumAsync(
            db.Set<Payment>().AsNoTracking().Where(p => p.Status == PaymentStatus.Recorded
                && periods.Contains(p.Period)
                && (groupId == null || p.GroupId == groupId)),
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> RecordedTotalsOfEnrollmentsAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollmentIds);
        ArgumentNullException.ThrowIfNull(firstPeriod);
        ArgumentNullException.ThrowIfNull(lastPeriod);
        if (enrollmentIds.Count == 0)
        {
            return new Dictionary<(EnrollmentId, YearMonth), decimal>();
        }

        var periods = PeriodsOf(firstPeriod, lastPeriod);
        var ids = enrollmentIds.Distinct().ToList();
        return await SumAsync(
            db.Set<Payment>().AsNoTracking().Where(p => p.Status == PaymentStatus.Recorded && periods.Contains(p.Period) && ids.Contains(p.EnrollmentId)),
            cancellationToken);
    }

    /// <summary>The window has at most a few dozen months (DEC-183), so the period list stays small.</summary>
    private static List<YearMonth> PeriodsOf(YearMonth firstPeriod, YearMonth lastPeriod)
    {
        var periods = new List<YearMonth>();
        for (var period = firstPeriod; period <= lastPeriod; period = period.Next())
        {
            periods.Add(period);
        }

        return periods;
    }

    private static async Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> SumAsync(
        IQueryable<Payment> payments,
        CancellationToken cancellationToken)
    {
        var totals = await payments
            .GroupBy(p => new { p.EnrollmentId, p.Period })
            .Select(g => new { g.Key.EnrollmentId, g.Key.Period, Total = g.Sum(p => p.Amount.Amount) })
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(t => (t.EnrollmentId, t.Period), t => t.Total);
    }

    public async Task<IReadOnlyDictionary<YearMonth, decimal>> CollectedByPaymentMonthAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        // Grouped by the payment date's month in the database; the (tenant_id, paid_on) index serves the range.
        var totals = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Recorded && p.PaidOn >= firstDay && p.PaidOn <= lastDay)
            .GroupBy(p => new { p.PaidOn.Year, p.PaidOn.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(p => p.Amount.Amount) })
            .ToListAsync(cancellationToken);

        return totals.ToDictionary(t => YearMonth.Create(t.Year, t.Month).Value, t => t.Total);
    }

    public Task<decimal> RecordedTotalAsync(EnrollmentId enrollmentId, YearMonth period, CancellationToken cancellationToken) =>
        db.Set<Payment>().AsNoTracking()
            .Where(p => p.EnrollmentId == enrollmentId && p.Period == period && p.Status == PaymentStatus.Recorded)
            .SumAsync(p => p.Amount.Amount, cancellationToken);
}
