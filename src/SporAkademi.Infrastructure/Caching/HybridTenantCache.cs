using Microsoft.Extensions.Caching.Hybrid;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Caching;

/// <summary>
/// <see cref="ITenantCache"/> on <see cref="HybridCache"/> (DEC-310): L1 in process, L2 in Valkey when a
/// <c>ConnectionStrings:Cache</c> is configured. HybridCache runs one factory per key at a time (stampede
/// protection) and serializes values with System.Text.Json.
/// </summary>
internal sealed class HybridTenantCache(HybridCache cache, ResilientDistributedCache distributed, ICurrentUser currentUser) : ITenantCache
{
    public ValueTask<T> GetOrCreateAsync<T>(
        TenantCacheKey key,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        // DEC-312: a school user can only read and fill its own school's entries. Only callers without a school
        // (Platform Admin, system work) may address a school explicitly, e.g. to read its time zone.
        if (currentUser.TenantId is { } tenant && tenant != key.Tenant.Value)
        {
            throw new TenantIsolationException("Cannot use another school's cache entries.");
        }

        // HybridCache may run the factory on a thread-pool work item that does not flow the execution context, which
        // would lose the request's user and with it the tenant query filter. The factory runs in the caller's context.
        var run = new ContextBoundFactory<T>(factory, ExecutionContext.Capture());
        return cache.GetOrCreateAsync(
            key.Value,
            run,
            static (state, ct) => state.InvokeAsync(ct),
            CachePolicies.For(key.Area, distributed.IsConfigured),
            key.Tags,
            cancellationToken);
    }

    private sealed class ContextBoundFactory<T>(Func<CancellationToken, ValueTask<T>> factory, ExecutionContext? context)
    {
        public ValueTask<T> InvokeAsync(CancellationToken cancellationToken)
        {
            if (context is null)
            {
                return factory(cancellationToken);
            }

            ValueTask<T> result = default;
            ExecutionContext.Run(context, _ => result = factory(cancellationToken), null);
            return result;
        }
    }
}

/// <summary>Lifetimes per area (DEC-314). L1 stays short so other API instances see an invalidation within a minute.</summary>
internal static class CachePolicies
{
    private static readonly Dictionary<(CacheArea, bool), HybridCacheEntryOptions> Options = Build();

    public static HybridCacheEntryOptions For(CacheArea area, bool withDistributedCache) => Options[(area, withDistributedCache)];

    public static (TimeSpan Distributed, TimeSpan Local) Lifetimes(CacheArea area) => area switch
    {
        CacheArea.School => (TimeSpan.FromHours(6), TimeSpan.FromMinutes(10)),
        CacheArea.Sports or CacheArea.Facilities => (TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1)),
        CacheArea.Dashboard => (TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30)),
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown cache area."),
    };

    private static Dictionary<(CacheArea, bool), HybridCacheEntryOptions> Build()
    {
        var options = new Dictionary<(CacheArea, bool), HybridCacheEntryOptions>();
        foreach (var area in Enum.GetValues<CacheArea>())
        {
            var (distributed, local) = Lifetimes(area);
            options[(area, true)] = new HybridCacheEntryOptions { Expiration = distributed, LocalCacheExpiration = local };
            // Without an L2 the entry lives only in memory; skipping the L2 also skips serializing for it.
            options[(area, false)] = new HybridCacheEntryOptions
            {
                Expiration = local,
                LocalCacheExpiration = local,
                Flags = HybridCacheEntryFlags.DisableDistributedCache,
            };
        }

        return options;
    }
}
