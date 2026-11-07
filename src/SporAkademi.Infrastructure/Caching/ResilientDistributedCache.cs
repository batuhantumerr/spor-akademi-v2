using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace SporAkademi.Infrastructure.Caching;

/// <summary>
/// The L2 of the cache (DEC-315): Valkey over the Redis protocol when <c>ConnectionStrings:Cache</c> is set,
/// otherwise nothing (L1 only). A failing L2 never fails a request: errors are logged without keys or values,
/// reads become misses and writes are dropped, and the L2 is skipped for <see cref="RetryAfter"/> so a dead
/// server does not add latency to every request. The database stays the source of truth.
/// </summary>
internal sealed partial class ResilientDistributedCache : IDistributedCache, IDisposable
{
    public const string ConnectionStringName = "Cache";
    public const string InstanceName = "spor-akademi:";

    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly RedisCache? _inner;
    private readonly ILogger<ResilientDistributedCache> _logger;
    private long _skipUntil;

    public ResilientDistributedCache(IConfiguration configuration, ILogger<ResilientDistributedCache> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var options = ConfigurationOptions.Parse(connectionString);
        // Start even when the server is down, and fail commands at once instead of queueing them until a timeout.
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ConnectTimeout = Math.Min(options.ConnectTimeout, 2_000);
        options.AsyncTimeout = Math.Min(options.AsyncTimeout, 1_000);
        options.SyncTimeout = Math.Min(options.SyncTimeout, 1_000);
        _inner = new RedisCache(Options.Create(new RedisCacheOptions { ConfigurationOptions = options, InstanceName = InstanceName }));
    }

    /// <summary>False when no L2 is configured; entries then live only in memory.</summary>
    public bool IsConfigured => _inner is not null;

    public byte[]? Get(string key) => Run(cache => cache.Get(key), default(byte[]));

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        RunAsync(cache => cache.GetAsync(key, token), default(byte[]));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        Run(cache => { cache.Set(key, value, options); return true; }, false);

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        RunAsync(async cache => { await cache.SetAsync(key, value, options, token); return true; }, false);

    public void Refresh(string key) => Run(cache => { cache.Refresh(key); return true; }, false);

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        RunAsync(async cache => { await cache.RefreshAsync(key, token); return true; }, false);

    public void Remove(string key) => Run(cache => { cache.Remove(key); return true; }, false);

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        RunAsync(async cache => { await cache.RemoveAsync(key, token); return true; }, false);

    public void Dispose() => _inner?.Dispose();

    /// <summary>False when no L2 is configured, or while it is skipped after a failure (writes are then dropped).</summary>
    public bool IsAvailable => _inner is not null && Environment.TickCount64 >= Interlocked.Read(ref _skipUntil);

    private T Run<T>(Func<RedisCache, T> operation, T fallback)
    {
        if (!IsAvailable)
        {
            return fallback;
        }

        try
        {
            return operation(_inner!);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            Trip(exception);
            return fallback;
        }
    }

    private async Task<T> RunAsync<T>(Func<RedisCache, Task<T>> operation, T fallback)
    {
        if (!IsAvailable)
        {
            return fallback;
        }

        try
        {
            return await operation(_inner!);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            Trip(exception);
            return fallback;
        }
    }

    private static bool IsCacheFailure(Exception exception) =>
        exception is RedisException or TimeoutException or System.Net.Sockets.SocketException or ObjectDisposedException;

    private void Trip(Exception exception)
    {
        Interlocked.Exchange(ref _skipUntil, Environment.TickCount64 + (long)RetryAfter.TotalMilliseconds);
        // The exception type only: messages can carry keys, and keys are not logged (NFR-PRV-03).
        LogUnavailable(_logger, exception.GetType().Name, RetryAfter.TotalSeconds);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distributed cache unavailable ({ErrorType}); using the database and the in-memory cache for {RetrySeconds} s")]
    private static partial void LogUnavailable(ILogger logger, string errorType, double retrySeconds);
}
