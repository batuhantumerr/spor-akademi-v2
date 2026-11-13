using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Tenancy;

/// <summary>
/// A feature that can be switched on or off per school (P4-10, DEC-580). The definitions live in code (<see
/// cref="FeatureCatalog"/>); a school's own value is a <see cref="SchoolFeature"/> row, and without one the global
/// default applies (configuration, then <see cref="DefaultEnabled"/>).
/// </summary>
/// <param name="Key">Stable key used by the API, the clients and the configuration, e.g. <c>reports</c>.</param>
/// <param name="Description">What the flag hides when it is off; for the API documentation, not for the interface.</param>
/// <param name="DefaultEnabled">The built-in default when neither the configuration nor the school sets a value.</param>
public sealed record FeatureDefinition(string Key, string Description, bool DefaultEnabled);

/// <summary>Every feature flag the platform knows. A key that is not here cannot be stored or read.</summary>
public static class FeatureCatalog
{
    public const int KeyMaxLength = 64;

    /// <summary>The School Admin reports page and its API (P3-09).</summary>
    public static readonly FeatureDefinition Reports = new(
        "reports",
        "School Admin reports (attendance, occupancy, revenue) and their CSV exports.",
        DefaultEnabled: true);

    /// <summary>Online payment of dues from the parent app (P3-03).</summary>
    public static readonly FeatureDefinition ParentOnlinePayments = new(
        "parent-online-payments",
        "Online payment of dues from the parent app.",
        DefaultEnabled: true);

    public static IReadOnlyList<FeatureDefinition> All { get; } = [Reports, ParentOnlinePayments];

    public static FeatureDefinition? Find(string? key) =>
        All.FirstOrDefault(feature => string.Equals(feature.Key, key, StringComparison.Ordinal));
}

public readonly record struct SchoolFeatureId(Guid Value) : IEntityId<SchoolFeatureId>
{
    public static SchoolFeatureId From(Guid value) => new(value);
}

/// <summary>
/// One school's own value of one feature flag, set by the Platform Admin (P4-10). Not tenant-scoped like the school
/// modules' aggregates: the Platform Admin, who has no school, writes it; school users only read their own school's
/// rows (row level security as a platform table, DEC-581).
/// </summary>
public sealed class SchoolFeature : AggregateRoot<SchoolFeatureId>
{
    private SchoolFeature(SchoolFeatureId id, SchoolId tenantId, string key, bool enabled)
        : base(id)
    {
        TenantId = tenantId;
        Key = key;
        Enabled = enabled;
    }

    // For EF Core materialization only.
    private SchoolFeature()
        : base(default) => Key = null!;

    public SchoolId TenantId { get; private set; }

    public string Key { get; private set; }

    public bool Enabled { get; private set; }

    public static Result<SchoolFeature> Create(SchoolId school, string key, bool enabled) =>
        FeatureCatalog.Find(key) is { } feature
            ? Result.Success(new SchoolFeature(EntityId.New<SchoolFeatureId>(), school, feature.Key, enabled))
            : Result.Failure<SchoolFeature>(FeatureErrors.NotFound);

    public void Set(bool enabled) => Enabled = enabled;
}

public static class FeatureErrors
{
    public static readonly Error NotFound = Error.NotFound("Feature.NotFound", "Feature not found.");
}
