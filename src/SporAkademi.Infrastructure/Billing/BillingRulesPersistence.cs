using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

internal sealed class BillingRulesConfiguration : IEntityTypeConfiguration<BillingRules>
{
    /// <summary>DEC-370: one version per school and period, like <c>ix_group_fees_group_id_effective_from</c>.</summary>
    public const string PeriodIndex = "ix_billing_rules_tenant_id_effective_from";

    public void Configure(EntityTypeBuilder<BillingRules> builder)
    {
        builder.ToTable("billing_rules", Schemas.Billing, t =>
        {
            t.HasCheckConstraint("ck_billing_rules_second_child_percent", "second_child_percent BETWEEN 0 AND 100");
            t.HasCheckConstraint("ck_billing_rules_third_and_later_child_percent", "third_and_later_child_percent BETWEEN 0 AND 100");
        });
        builder.HasKey(r => r.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.TenantId, r.EffectiveFrom }).IsUnique().HasDatabaseName(PeriodIndex);
        builder.Ignore(r => r.Terms);
        builder.Ignore(r => r.DomainEvents);
    }
}

internal sealed class BillingRulesRepository(AppDbContext db) : IBillingRulesRepository
{
    public async Task<IReadOnlyList<BillingRules>> ListAsync(CancellationToken cancellationToken) =>
        await db.Set<BillingRules>().AsNoTracking().OrderByDescending(r => r.EffectiveFrom).ToListAsync(cancellationToken);

    public Task<bool> ExistsAsync(YearMonth effectiveFrom, CancellationToken cancellationToken) =>
        db.Set<BillingRules>().AnyAsync(r => r.EffectiveFrom == effectiveFrom, cancellationToken);

    public void Add(BillingRules rules) => db.Add(rules);

    public Task<BillingRules?> GetAsync(YearMonth effectiveFrom, CancellationToken cancellationToken) =>
        db.Set<BillingRules>().SingleOrDefaultAsync(r => r.EffectiveFrom == effectiveFrom, cancellationToken);

    public void Remove(BillingRules rules) => db.Remove(rules);
}

internal sealed class ChargeSnapshotConfiguration : IEntityTypeConfiguration<ChargeSnapshot>
{
    /// <summary>DEC-375: one frozen charge per enrollment and period.</summary>
    public const string UniqueIndex = "ix_charge_snapshots_tenant_id_enrollment_id_period";

    public void Configure(EntityTypeBuilder<ChargeSnapshot> builder)
    {
        builder.ToTable("charge_snapshots", Schemas.Billing, t =>
        {
            t.HasCheckConstraint("ck_charge_snapshots_amounts", "fee_amount >= 0 AND discount_amount >= 0 AND fee_amount + discount_amount = list_fee_amount");
            t.HasCheckConstraint("ck_charge_snapshots_discount_percent", "discount_percent BETWEEN 0 AND 100");
        });
        builder.HasKey(s => s.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);

        // ADR-0003 layer 3, like payments (DEC-185): the snapshot's enrollment, student and group belong to its own school.
        builder.HasOne<Enrollment>().WithMany()
            .HasForeignKey(s => new { s.TenantId, s.EnrollmentId, s.StudentId, s.GroupId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id, e.StudentId, e.GroupId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.ComplexProperty(s => s.ListFee, m => MoneyMapping.Configure(m, "list_fee_amount", "list_fee_currency"));
        builder.ComplexProperty(s => s.Discount, m => MoneyMapping.Configure(m, "discount_amount", "discount_currency"));
        builder.ComplexProperty(s => s.Fee, m => MoneyMapping.Configure(m, "fee_amount", "fee_currency"));

        builder.HasIndex(s => new { s.TenantId, s.EnrollmentId, s.Period }).IsUnique().HasDatabaseName(UniqueIndex);
        builder.HasIndex(s => new { s.TenantId, s.Period });
        builder.HasIndex(s => new { s.TenantId, s.StudentId, s.Period });
        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class ClosedPeriodConfiguration : IEntityTypeConfiguration<ClosedPeriod>
{
    public const string UniqueIndex = "ix_closed_periods_tenant_id_period";

    public void Configure(EntityTypeBuilder<ClosedPeriod> builder)
    {
        builder.ToTable("closed_periods", Schemas.Billing, t => t.HasCheckConstraint("ck_closed_periods_snapshot_count", "snapshot_count >= 0"));
        builder.HasKey(p => p.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.Period }).IsUnique().HasDatabaseName(UniqueIndex);
        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class ChargeSnapshotRepository(AppDbContext db) : IChargeSnapshotRepository
{
    public async Task<IReadOnlySet<YearMonth>> ClosedPeriodsAsync(YearMonth firstPeriod, YearMonth lastPeriod, CancellationToken cancellationToken)
    {
        var periods = Periods(firstPeriod, lastPeriod);
        var closed = await db.Set<ClosedPeriod>().AsNoTracking()
            .Where(p => periods.Contains(p.Period))
            .Select(p => p.Period)
            .ToListAsync(cancellationToken);
        return closed.ToHashSet();
    }

    public async Task<IReadOnlyList<ChargeSnapshot>> ListAsync(YearMonth firstPeriod, YearMonth lastPeriod, CancellationToken cancellationToken)
    {
        var periods = Periods(firstPeriod, lastPeriod);
        return await db.Set<ChargeSnapshot>().AsNoTracking().Where(s => periods.Contains(s.Period)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChargeSnapshot>> ListOfStudentsBetweenAsync(
        IReadOnlyCollection<StudentId> studentIds,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studentIds);
        var ids = studentIds.ToList();
        var periods = Periods(firstPeriod, lastPeriod);
        return await db.Set<ChargeSnapshot>().AsNoTracking()
            .Where(s => ids.Contains(s.StudentId) && periods.Contains(s.Period))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChargeSnapshot>> ListOfStudentsAsync(IReadOnlyCollection<StudentId> studentIds, YearMonth period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studentIds);
        var ids = studentIds.ToList();
        return await db.Set<ChargeSnapshot>().AsNoTracking()
            .Where(s => ids.Contains(s.StudentId) && s.Period == period)
            .ToListAsync(cancellationToken);
    }

    public void Add(ChargeSnapshot snapshot) => db.Add(snapshot);

    public void Add(ClosedPeriod period) => db.Add(period);

    /// <summary>The window is at most a few years of months, so the list stays small; the filter runs in SQL.</summary>
    private static List<YearMonth> Periods(YearMonth firstPeriod, YearMonth lastPeriod)
    {
        ArgumentNullException.ThrowIfNull(firstPeriod);
        ArgumentNullException.ThrowIfNull(lastPeriod);
        var periods = new List<YearMonth>();
        for (var period = firstPeriod; period <= lastPeriod; period = period.Next())
        {
            periods.Add(period);
        }

        return periods;
    }
}
