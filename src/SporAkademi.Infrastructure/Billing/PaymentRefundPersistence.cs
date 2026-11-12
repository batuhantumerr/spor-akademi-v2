using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
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
            // DEC-418: only a pending request waits for review.
            t.HasCheckConstraint("ck_payment_refunds_review", "NOT needs_review OR status = 'Pending'");
            // DEC-418: a manual settlement always has its author, time and note, and only on a final request.
            t.HasCheckConstraint(
                "ck_payment_refunds_resolution",
                "(resolved_at IS NULL) = (resolved_by IS NULL) AND (resolved_at IS NULL) = (resolution_note IS NULL) AND (resolved_at IS NULL OR status <> 'Pending')");
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
        builder.Property(r => r.ResolutionNote).HasMaxLength(PaymentRefund.ReasonMaxLength);

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
            .Where(r => r.Status == PaymentRefundStatus.Pending && !r.NeedsReview && (r.LastAttemptAt ?? r.RequestedAt) < before)
            .OrderBy(r => r.RequestedAt).ThenBy(r => r.Id)
            .Take(max)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryClaimAttemptAsync(
        PaymentRefundId id,
        int expectedAttempts,
        DateTimeOffset now,
        DateTimeOffset? lastAttemptBefore,
        CancellationToken cancellationToken)
    {
        // DEC-419: one conditional UPDATE, committed at once outside any transaction of the caller (the refund flow never
        // holds one here). Exactly one caller sees one changed row; the tenant filter applies.
        var claimed = await db.Set<PaymentRefund>()
            .Where(r => r.Id == id && r.Status == PaymentRefundStatus.Pending && r.Attempts == expectedAttempts
                && (lastAttemptBefore == null || (r.LastAttemptAt ?? r.RequestedAt) < lastAttemptBefore))
            .ExecuteUpdateAsync(
                set => set.SetProperty(r => r.Attempts, r => r.Attempts + 1)
                    .SetProperty(r => r.LastAttemptAt, now)
                    .SetProperty(r => EF.Property<DateTimeOffset>(r, AuditColumns.UpdatedAt), now),
                cancellationToken);
        // The request that created the row may still track it in this scope (same DbContext): refresh its attempts and
        // xmin, or storing the provider's answer would fail with a concurrency conflict.
        if (db.ChangeTracker.Entries<PaymentRefund>().FirstOrDefault(e => e.Entity.Id == id) is { } tracked)
        {
            await tracked.ReloadAsync(cancellationToken);
        }

        return claimed == 1;
    }

    public async Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> PendingRefundTotalsAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollmentIds);
        var ids = enrollmentIds.Distinct().ToList();
        var totals = await db.Set<PaymentRefund>().AsNoTracking()
            .Where(r => r.Status == PaymentRefundStatus.Pending && ids.Contains(r.EnrollmentId))
            .Join(
                db.Set<Payment>().AsNoTracking().Where(p => p.Status == PaymentStatus.Recorded),
                r => r.PaymentId,
                p => p.Id,
                (r, p) => new { p.EnrollmentId, p.Period, Amount = p.Amount.Amount })
            .GroupBy(x => new { x.EnrollmentId, x.Period })
            .Select(g => new { g.Key.EnrollmentId, g.Key.Period, Total = g.Sum(x => x.Amount) })
            .ToListAsync(cancellationToken);
        return totals.ToDictionary(t => (t.EnrollmentId, t.Period), t => t.Total);
    }

    public void Add(PaymentRefund refund) => db.Add(refund);
}
