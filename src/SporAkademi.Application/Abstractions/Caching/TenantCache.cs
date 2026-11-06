using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Abstractions.Caching;

/// <summary>
/// The cached read models (DEC-311). Each area is one school's copy of one read; a write to the area's data
/// invalidates that school's entries of the area after the commit (DEC-313).
/// </summary>
public enum CacheArea
{
    /// <summary>The school's own settings (its time zone). Changes with the school row.</summary>
    School,

    /// <summary>The school's sport lists.</summary>
    Sports,

    /// <summary>The school's facility lists.</summary>
    Facilities,

    /// <summary>The School Admin dashboard; any write of the school invalidates it.</summary>
    Dashboard,
}

/// <summary>
/// A cache key that always belongs to one school (DEC-312). It is the only way to address the cache, so tenant
/// data cannot be cached without its tenant: the key text starts with the school id and the entry carries the
/// school's area tag and tenant tag. Key parts are booleans, integers, dates or short constant names, never free
/// text, so no personal data can end up in a key.
/// </summary>
public sealed partial class TenantCacheKey
{
    /// <summary>Bumped when the shape of a cached DTO changes incompatibly, so old entries are never read.</summary>
    public const string Prefix = "sa:v1";

    public const int MaxLength = 200;

    private TenantCacheKey(SchoolId tenant, CacheArea area, string value)
    {
        Tenant = tenant;
        Area = area;
        Value = value;
        Tags = [AreaTag(tenant, area), TenantTag(tenant)];
    }

    public SchoolId Tenant { get; }

    public CacheArea Area { get; }

    /// <summary>The full key, e.g. <c>sa:v1:t:{school id}:sports:list:0:1:20</c>.</summary>
    public string Value { get; }

    /// <summary>The area tag (used for invalidation) and the tenant tag (for removing all of a school's entries).</summary>
    public IReadOnlyList<string> Tags { get; }

    public static TenantCacheKey For(SchoolId tenant, CacheArea area, params ReadOnlySpan<object> parts)
    {
        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("A cache key needs the school it belongs to.", nameof(tenant));
        }

        var key = new StringBuilder(AreaTag(tenant, area));
        foreach (var part in parts)
        {
            key.Append(':').Append(FormatPart(part));
        }

        return key.Length <= MaxLength
            ? new TenantCacheKey(tenant, area, key.ToString())
            : throw new ArgumentException($"A cache key may have at most {MaxLength} characters.", nameof(parts));
    }

    public static string TenantTag(SchoolId tenant) =>
        tenant.Value == Guid.Empty
            ? throw new ArgumentException("A cache tag needs the school it belongs to.", nameof(tenant))
            : string.Create(CultureInfo.InvariantCulture, $"{Prefix}:t:{tenant.Value:N}");

    public static string AreaTag(SchoolId tenant, CacheArea area) => $"{TenantTag(tenant)}:{AreaName(area)}";

    public override string ToString() => Value;

    private static string AreaName(CacheArea area) => area switch
    {
        CacheArea.School => "school",
        CacheArea.Sports => "sports",
        CacheArea.Facilities => "facilities",
        CacheArea.Dashboard => "dashboard",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown cache area."),
    };

    private static string FormatPart(object part) => part switch
    {
        bool flag => flag ? "1" : "0",
        int number => number.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string name when NamePart().IsMatch(name) => name,
        _ => throw new ArgumentException(
            "Cache key parts are booleans, integers, dates or constant names of [a-z0-9-], never free text or personal data.",
            nameof(part)),
    };

    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePart();
}

/// <summary>
/// Read-through cache for tenant-owned read models (HybridCache: in-memory L1, optional Redis-protocol L2; DEC-310).
/// Cache only what the same school's callers may read anyway, never per-user results or authorization decisions.
/// Invalidation is automatic: saving a change to an area's data removes the school's entries after the commit.
/// </summary>
public interface ITenantCache
{
    /// <summary>
    /// Returns the cached value or runs <paramref name="factory"/> once per key (concurrent callers wait for the same
    /// run). The value is serialized with System.Text.Json, so it must be a plain DTO.
    /// </summary>
    ValueTask<T> GetOrCreateAsync<T>(
        TenantCacheKey key,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken);
}
