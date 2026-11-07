using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>A panel session handed to the browser once, as an HttpOnly cookie.</summary>
public sealed record JobsDashboardSession(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Server-side job panel sessions (DEC-332). The browser holds a random 256-bit token in a cookie; only its SHA-256
/// hash is kept here, with the Platform Admin it belongs to and a fixed expiry (no sliding). A new session ends the
/// admin's previous one. Sessions live in this process's memory: a restart ends them, and with several API
/// instances the panel must be served by the instance that issued the cookie (open point for the Deploy block).
/// </summary>
public sealed class JobsDashboardSessions(TimeProvider clock, IOptions<JobsOptions> options)
{
    public const string CookieName = "sa_jobs";
    public const string Path = "/jobs";

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public JobsDashboardSession Create(Guid userId)
    {
        var now = clock.GetUtcNow();
        foreach (var (hash, entry) in _sessions)
        {
            if (entry.UserId == userId || entry.ExpiresAt <= now)
            {
                _sessions.TryRemove(hash, out _);
            }
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new JobsDashboardSession(token, now + options.Value.Dashboard.SessionLifetime);
        _sessions[Hash(token)] = new Entry(userId, session.ExpiresAt);
        return session;
    }

    /// <returns>The Platform Admin of a live session, or <c>null</c> for an unknown or expired token.</returns>
    public Guid? Find(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var hash = Hash(token);
        if (!_sessions.TryGetValue(hash, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= clock.GetUtcNow())
        {
            _sessions.TryRemove(hash, out _);
            return null;
        }

        return entry.UserId;
    }

    public void End(Guid userId)
    {
        foreach (var (hash, entry) in _sessions)
        {
            if (entry.UserId == userId)
            {
                _sessions.TryRemove(hash, out _);
            }
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Entry(Guid UserId, DateTimeOffset ExpiresAt);
}

/// <summary>Whether a user may (still) use the job panel: an active Platform Admin, checked on every panel request.</summary>
public interface IJobsDashboardAccess
{
    Task<bool> IsActivePlatformAdminAsync(Guid userId, CancellationToken cancellationToken);
}

internal sealed class JobsDashboardAccess(AppDbContext dbContext) : IJobsDashboardAccess
{
    public Task<bool> IsActivePlatformAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        var id = UserId.From(userId);
        return dbContext.Set<User>().AsNoTracking()
            .AnyAsync(u => u.Id == id && u.Role == UserRole.PlatformAdmin && u.Status == UserStatus.Active && u.TenantId == null, cancellationToken);
    }
}
