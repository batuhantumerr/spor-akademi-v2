using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Caching;

namespace SporAkademi.Infrastructure.Caching;

internal static class CacheDependencyInjection
{
    /// <summary>
    /// HybridCache with an optional Valkey L2 (DEC-310, DEC-315). The connection string is read when the cache is
    /// first resolved, like the database's, so tests and local runs without it get an in-memory cache only.
    /// </summary>
    public static IServiceCollection AddTenantCache(this IServiceCollection services)
    {
        services.AddSingleton<ResilientDistributedCache>();
        services.AddSingleton<CacheHost>();
        services.AddSingleton(provider => provider.GetRequiredService<CacheHost>().Cache);
        services.AddScoped<ITenantCache, HybridTenantCache>();
        services.AddSingleton<CacheTagInvalidator>();
        services.AddScoped<CacheInvalidationInterceptor>();
        return services;
    }
}

/// <summary>
/// Builds the <see cref="HybridCache"/> in its own small container (DEC-314) so that it
/// <list type="bullet">
/// <item>runs on the real clock: HybridCache takes <see cref="TimeProvider"/> from DI for expirations and tag
/// invalidation times, while the application's (domain) clock may be frozen or moved, e.g. in tests;</item>
/// <item>is the only user of the L2: <see cref="IDistributedCache"/> is not registered in the application container,
/// so nothing else can write tenant data to Valkey past <see cref="ITenantCache"/>.</item>
/// </list>
/// </summary>
internal sealed class CacheHost : IDisposable
{
    /// <summary>DEC-314: the largest cached value is a 100-row page or the dashboard; anything bigger is not cached.</summary>
    public const int MaximumPayloadBytes = 512 * 1024;

    private readonly ServiceProvider _provider;

    public CacheHost(ResilientDistributedCache distributed, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedCache>(distributed);
        services.AddHybridCache(options =>
        {
            options.MaximumKeyLength = TenantCacheKey.MaxLength;
            options.MaximumPayloadBytes = MaximumPayloadBytes;
        });
        _provider = services.BuildServiceProvider();
        Cache = _provider.GetRequiredService<HybridCache>();
    }

    public HybridCache Cache { get; }

    public void Dispose() => _provider.Dispose();
}
