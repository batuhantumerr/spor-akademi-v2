using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Tenancy;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

/// <summary>
/// <c>tenancy.school_features</c> (P4-10): one row per school and flag the Platform Admin has set. The school is the
/// row's <c>tenant_id</c>; a row is unique per school and key, and row level security treats it as a platform table
/// (DEC-581): a school sees its own rows, the Platform Admin all of them.
/// </summary>
internal sealed class SchoolFeatureConfiguration : IEntityTypeConfiguration<SchoolFeature>
{
    public void Configure(EntityTypeBuilder<SchoolFeature> builder)
    {
        builder.ToTable("school_features", Schemas.Tenancy);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Key).HasMaxLength(FeatureCatalog.KeyMaxLength);
        builder.HasIndex(f => new { f.TenantId, f.Key }).IsUnique();
        builder.HasOne<School>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(f => f.DomainEvents);
    }
}

internal sealed class SchoolFeatureRepository(AppDbContext dbContext) : ISchoolFeatureRepository
{
    public Task<Dictionary<string, bool>> FindValuesAsync(SchoolId school, CancellationToken cancellationToken) =>
        dbContext.Set<SchoolFeature>().AsNoTracking()
            .Where(f => f.TenantId == school)
            .ToDictionaryAsync(f => f.Key, f => f.Enabled, StringComparer.Ordinal, cancellationToken);

    public Task<SchoolFeature?> GetAsync(SchoolId school, string key, CancellationToken cancellationToken) =>
        dbContext.Set<SchoolFeature>().SingleOrDefaultAsync(f => f.TenantId == school && f.Key == key, cancellationToken);

    public void Add(SchoolFeature feature) => dbContext.Add(feature);
}

/// <summary>The <c>Features</c> configuration section (DEC-582): <c>Features:Defaults:{key}</c> = true/false.</summary>
internal sealed class FeatureOptions
{
    public const string Section = "Features";

    /// <summary>Text values: the binder would silently skip a value that is not a boolean, so <see cref="IsValid"/> parses them.</summary>
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal);

    /// <summary>Every configured key is a catalog key (keys are case-sensitive, like the API's) and every value a boolean.</summary>
    public bool IsValid() => Defaults.All(d => FeatureCatalog.Find(d.Key) is not null && bool.TryParse(d.Value, out _));
}

/// <summary>
/// The global defaults (DEC-582): the configuration per environment, else the catalog's default. The section is
/// validated at start: an unknown key or a value that is not a boolean stops the start, so a typo cannot silently leave
/// a feature at its built-in default.
/// </summary>
internal sealed class ConfiguredFeatureDefaults(IOptions<FeatureOptions> options) : IFeatureDefaults
{
    public bool IsEnabledByDefault(FeatureDefinition feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return options.Value.Defaults.TryGetValue(feature.Key, out var configured) && bool.TryParse(configured, out var enabled)
            ? enabled
            : feature.DefaultEnabled;
    }
}
