using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

internal sealed class AnonymizationRunConfiguration : IEntityTypeConfiguration<AnonymizationRun>
{
    public void Configure(EntityTypeBuilder<AnonymizationRun> builder)
    {
        builder.ToTable("anonymization_runs", Schemas.Students);
        builder.HasKey(r => r.Id);
        builder.HasOne<School>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        // The settings page lists a school's latest runs.
        builder.HasIndex(r => new { r.TenantId, r.StartedAt });
        builder.Ignore(r => r.DomainEvents);
    }
}

internal sealed class AnonymizationRunRepository(AppDbContext db) : IAnonymizationRunRepository
{
    public void Add(AnonymizationRun run) => db.Add(run);

    public async Task<IReadOnlyList<AnonymizationRunDto>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await db.Set<AnonymizationRun>().AsNoTracking()
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
            .Take(limit)
            .Select(r => new AnonymizationRunDto(
                r.Id.Value, r.StartedAt, r.DurationMs, r.Cutoff, r.PersonalDataMonths, r.AnonymizedCount, r.SkippedCount, r.Status))
            .ToListAsync(cancellationToken);
}
