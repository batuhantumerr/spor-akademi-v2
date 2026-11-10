using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;
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
}
