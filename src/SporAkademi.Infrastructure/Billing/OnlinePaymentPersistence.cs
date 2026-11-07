using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

internal sealed class OnlinePaymentConfiguration : IEntityTypeConfiguration<OnlinePayment>
{
    /// <summary>One open attempt per due (DEC-342); a second concurrent start is refused by the database.</summary>
    public const string OpenDueIndex = "ix_online_payments_open_due";

    public const string IdempotencyKeyIndex = "ix_online_payments_tenant_id_idempotency_key";

    public const string ProviderPaymentIndex = "ix_online_payments_provider_provider_payment_id";

    public void Configure(EntityTypeBuilder<OnlinePayment> builder)
    {
        builder.ToTable("online_payments", Schemas.Billing, t =>
        {
            t.HasCheckConstraint("ck_online_payments_amount", "amount > 0");
            // A success always has the provider's charge id and the recorded payment; nothing else has a payment.
            t.HasCheckConstraint(
                "ck_online_payments_success",
                "(status = 'Succeeded') = (payment_id IS NOT NULL) AND (status <> 'Succeeded' OR provider_payment_id IS NOT NULL)");
            t.HasCheckConstraint("ck_online_payments_overpaid", "NOT overpaid OR status = 'Succeeded'");
            t.HasCheckConstraint("ck_online_payments_checkout", "status IN ('Created', 'Failed', 'Expired') OR provider_token IS NOT NULL");
        });
        builder.HasKey(p => p.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3, as for payments (DEC-185): the enrollment and its denormalized student and group together.
        builder.HasOne<Enrollment>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.EnrollmentId, p.StudentId, p.GroupId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id, e.StudentId, e.GroupId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(p => p.Amount, MoneyMapping.Configure);
        builder.Property(p => p.Provider).HasMaxLength(OnlinePayment.ProviderMaxLength);
        builder.Property(p => p.IdempotencyKey).HasMaxLength(OnlinePayment.IdempotencyKeyMaxLength);
        builder.Property(p => p.RequestHash).HasMaxLength(OnlinePayment.RequestHashLength).IsFixedLength();
        builder.Property(p => p.ProviderToken).HasMaxLength(OnlinePayment.ProviderReferenceMaxLength);
        builder.Property(p => p.ProviderPaymentId).HasMaxLength(OnlinePayment.ProviderReferenceMaxLength);
        builder.Property(p => p.CheckoutUrl).HasMaxLength(OnlinePayment.CheckoutUrlMaxLength);
        builder.Property(p => p.FailureCode).HasMaxLength(OnlinePayment.FailureCodeMaxLength);
        builder.Ignore(p => p.IsOpen);

        // DEC-341: the Idempotency-Key is unique per school.
        builder.HasIndex(p => new { p.TenantId, p.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        // DEC-344: the anonymous webhook and callback find the attempt (and so its school) by the provider's token.
        builder.HasIndex(p => new { p.Provider, p.ProviderToken }).IsUnique().HasFilter("provider_token IS NOT NULL");
        // DEC-343: one provider charge belongs to at most one attempt.
        builder.HasIndex(p => new { p.Provider, p.ProviderPaymentId }).IsUnique().HasFilter("provider_payment_id IS NOT NULL")
            .HasDatabaseName(ProviderPaymentIndex);
        builder.HasIndex(p => new { p.TenantId, p.EnrollmentId, p.Period }).IsUnique()
            .HasFilter("status IN ('Created', 'Pending')").HasDatabaseName(OpenDueIndex);
        builder.HasIndex(p => new { p.TenantId, p.StartedAt });
        builder.HasIndex(p => p.PaymentId).IsUnique().HasFilter("payment_id IS NOT NULL");
        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class PaymentNotificationConfiguration : IEntityTypeConfiguration<PaymentNotification>
{
    /// <summary>DEC-343: a provider event is applied once.</summary>
    public const string EventKeyIndex = "ix_payment_notifications_provider_event_key";

    public void Configure(EntityTypeBuilder<PaymentNotification> builder)
    {
        builder.ToTable("payment_notifications", Schemas.Billing);
        builder.HasKey(n => n.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OnlinePayment>().WithMany()
            .HasForeignKey(n => new { n.TenantId, n.OnlinePaymentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(n => n.Provider).HasMaxLength(OnlinePayment.ProviderMaxLength);
        builder.Property(n => n.EventKey).HasMaxLength(PaymentNotification.EventKeyMaxLength);
        builder.HasIndex(n => new { n.Provider, n.EventKey }).IsUnique().HasDatabaseName(EventKeyIndex);
        builder.Ignore(n => n.DomainEvents);
    }
}

internal sealed class OnlinePaymentRepository(AppDbContext db) : IOnlinePaymentRepository
{
    public Task<OnlinePayment?> GetAsync(OnlinePaymentId id, CancellationToken cancellationToken) =>
        db.Set<OnlinePayment>().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<OnlinePayment?> FindAsync(OnlinePaymentId id, CancellationToken cancellationToken) =>
        db.Set<OnlinePayment>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<OnlinePayment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Set<OnlinePayment>().AsNoTracking().SingleOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<OnlinePayment?> FindOpenAsync(EnrollmentId enrollmentId, YearMonth period, CancellationToken cancellationToken) =>
        db.Set<OnlinePayment>().AsNoTracking().SingleOrDefaultAsync(
            p => p.EnrollmentId == enrollmentId && p.Period == period
                && (p.Status == OnlinePaymentStatus.Created || p.Status == OnlinePaymentStatus.Pending),
            cancellationToken);

    public Task ExpireAsync(OnlinePaymentId id, DateTimeOffset now, CancellationToken cancellationToken) =>
        // Runs at once inside the unit of work's transaction, before the replacing attempt is inserted, so the open-due
        // index never sees two open attempts. The tenant filter applies to the update.
        db.Set<OnlinePayment>()
            .Where(p => p.Id == id && (p.Status == OnlinePaymentStatus.Created || p.Status == OnlinePaymentStatus.Pending))
            .ExecuteUpdateAsync(
                set => set.SetProperty(p => p.Status, OnlinePaymentStatus.Expired).SetProperty(p => p.CompletedAt, now),
                cancellationToken);

    public void Add(OnlinePayment payment) => db.Add(payment);

    public async Task<PagedResult<OnlinePayment>> ListAsync(OnlinePaymentFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var query = db.Set<OnlinePayment>().AsNoTracking();
        if (filter.EnrollmentId is { } enrollmentId)
        {
            query = query.Where(p => p.EnrollmentId == enrollmentId);
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
        var items = await query.OrderByDescending(p => p.StartedAt).ThenByDescending(p => p.Id)
            .Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<OnlinePayment>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<OnlinePayment>> ListOpenStartedBeforeAsync(DateTimeOffset startedBefore, int max, CancellationToken cancellationToken) =>
        await db.Set<OnlinePayment>().AsNoTracking()
            .Where(p => (p.Status == OnlinePaymentStatus.Created || p.Status == OnlinePaymentStatus.Pending) && p.StartedAt < startedBefore)
            .OrderBy(p => p.StartedAt).ThenBy(p => p.Id)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task<bool> NotificationExistsAsync(string provider, string eventKey, CancellationToken cancellationToken) =>
        db.Set<PaymentNotification>().AnyAsync(n => n.Provider == provider && n.EventKey == eventKey, cancellationToken);

    public void Add(PaymentNotification notification) => db.Add(notification);
}
