using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

internal sealed class PaymentRefundConfiguration : IEntityTypeConfiguration<PaymentRefund>
{
    /// <summary>DEC-419: the Idempotency-Key is unique per school.</summary>
    public const string IdempotencyKeyIndex = "ix_payment_refunds_tenant_id_idempotency_key";

    /// <summary>DEC-419: at most one pending or succeeded refund per payment; a second concurrent request is refused here.</summary>
    public const string ActivePaymentIndex = "ix_payment_refunds_active_payment";

    public void Configure(EntityTypeBuilder<PaymentRefund> builder)
    {
        builder.ToTable("payment_refunds", Schemas.Billing, t =>
        {
            t.HasCheckConstraint("ck_payment_refunds_amount", "amount > 0");
            // A success always has the provider's reference, a refusal its code, and both their time; a pending one none.
            t.HasCheckConstraint(
                "ck_payment_refunds_result",
                "(status = 'Succeeded') = (provider_refund_id IS NOT NULL) AND (status = 'Failed') = (failure_code IS NOT NULL) AND (status = 'Pending') = (completed_at IS NULL)");
            t.HasCheckConstraint("ck_payment_refunds_attempts", "attempts >= 0");
        });
        builder.HasKey(r => r.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3: the payment, its online attempt and the enrollment are of the same school.
        builder.HasOne<Payment>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.PaymentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OnlinePayment>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.OnlinePaymentId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Enrollment>().WithMany()
            .HasForeignKey(r => new { r.TenantId, r.EnrollmentId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(r => r.Amount, MoneyMapping.Configure);
        builder.Property(r => r.Provider).HasMaxLength(OnlinePayment.ProviderMaxLength);
        builder.Property(r => r.ProviderPaymentId).HasMaxLength(OnlinePayment.ProviderReferenceMaxLength);
        builder.Property(r => r.ProviderRefundId).HasMaxLength(OnlinePayment.ProviderReferenceMaxLength);
        builder.Property(r => r.Reason).HasMaxLength(PaymentRefund.ReasonMaxLength);
        builder.Property(r => r.IdempotencyKey).HasMaxLength(OnlinePayment.IdempotencyKeyMaxLength);
        builder.Property(r => r.RequestHash).HasMaxLength(OnlinePayment.RequestHashLength).IsFixedLength();
        builder.Property(r => r.FailureCode).HasMaxLength(PaymentRefund.FailureCodeMaxLength);

        builder.HasIndex(r => new { r.TenantId, r.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(r => r.PaymentId).IsUnique().HasFilter("status IN ('Pending', 'Succeeded')").HasDatabaseName(ActivePaymentIndex);
        builder.HasIndex(r => new { r.TenantId, r.PaymentId, r.RequestedAt });
        builder.HasIndex(r => new { r.TenantId, r.RequestedAt }).HasFilter("status = 'Pending'");
        builder.Ignore(r => r.DomainEvents);
    }
}

internal sealed class PaymentRefundRepository(AppDbContext db) : IPaymentRefundRepository
{
    public Task<PaymentRefund?> GetAsync(PaymentRefundId id, CancellationToken cancellationToken) =>
        db.Set<PaymentRefund>().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<PaymentRefund?> FindAsync(PaymentRefundId id, CancellationToken cancellationToken) =>
        db.Set<PaymentRefund>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<PaymentRefund?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Set<PaymentRefund>().AsNoTracking().SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<PaymentRefund?> FindActiveAsync(PaymentId paymentId, CancellationToken cancellationToken) =>
        db.Set<PaymentRefund>().AsNoTracking().SingleOrDefaultAsync(
            r => r.PaymentId == paymentId && (r.Status == PaymentRefundStatus.Pending || r.Status == PaymentRefundStatus.Succeeded),
            cancellationToken);

    public async Task<IReadOnlyDictionary<PaymentId, PaymentRefund>> LatestOfPaymentsAsync(IReadOnlyCollection<PaymentId> paymentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paymentIds);
        var ids = paymentIds.Distinct().ToList();
        // A payment has few requests (failures and one success at most), so all are read and the newest kept.
        var all = await db.Set<PaymentRefund>().AsNoTracking().Where(r => ids.Contains(r.PaymentId)).ToListAsync(cancellationToken);
        return all.GroupBy(r => r.PaymentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id.Value).First());
    }

    public async Task<IReadOnlyList<PaymentRefund>> ListPendingSinceBeforeAsync(DateTimeOffset before, int max, CancellationToken cancellationToken) =>
        await db.Set<PaymentRefund>().AsNoTracking()
            .Where(r => r.Status == PaymentRefundStatus.Pending && (r.LastAttemptAt ?? r.RequestedAt) < before)
            .OrderBy(r => r.RequestedAt).ThenBy(r => r.Id)
            .Take(max)
            .ToListAsync(cancellationToken);

    public void Add(PaymentRefund refund) => db.Add(refund);
}
