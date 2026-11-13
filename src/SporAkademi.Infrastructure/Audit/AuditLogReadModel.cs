using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Audit;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Audit;

/// <summary>The current school's audit entries (query filter and row level security), with the actors' names.</summary>
internal sealed class AuditLogReadModel(AppDbContext db) : IAuditLogReadModel
{
    public async Task<PagedResult<AuditEntryDto>> ListAsync(AuditLogFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var query = db.Set<AuditEntry>().AsNoTracking();
        if (filter.EntityType is { } type)
        {
            query = query.Where(e => e.EntityType == type);
        }

        if (filter.UserId is { } user)
        {
            query = query.Where(e => e.UserId == user);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.Until is { } until)
        {
            query = query.Where(e => e.OccurredAt < until);
        }

        var total = await query.CountAsync(cancellationToken);
        var entries = await query
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(entries.Select(e => e.UserId), cancellationToken);

        return new PagedResult<AuditEntryDto>(
            [.. entries.Select(e => new AuditEntryDto(
                e.Id,
                e.OccurredAt,
                e.UserId,
                e.UserId is { } id ? names.GetValueOrDefault(id) : null,
                e.UserRole,
                e.Action,
                e.EntityType,
                e.EntityId,
                e.Fields,
                e.Reason,
                e.CorrelationId))],
            page.Page,
            page.PageSize,
            total);
    }

    public async Task<IReadOnlyList<AuditActorDto>> ListActorsAsync(CancellationToken cancellationToken)
    {
        var actors = await db.Set<AuditEntry>().AsNoTracking()
            .Where(e => e.UserId != null)
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key!.Value, Role = g.Max(e => e.UserRole) })
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(actors.Select(a => (Guid?)a.UserId), cancellationToken);
        return [.. actors
            .Select(a => new AuditActorDto(a.UserId, names.GetValueOrDefault(a.UserId), a.Role))
            .OrderBy(a => a.UserName ?? "￿", StringComparer.CurrentCulture)
            .ThenBy(a => a.UserId)];
    }

    /// <summary>Users of the school only (row level security on <c>identity.users</c>); someone else stays unnamed.</summary>
    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.OfType<Guid>().Distinct().Select(UserId.From).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Set<User>().AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .ToListAsync(cancellationToken);
        return users.ToDictionary(u => u.Id.Value, u => $"{u.FirstName} {u.LastName}");
    }
}

/// <summary>
/// P4-05 (DEC-536): once a day, deletes audit entries older than two years through <c>audit.purge_expired</c>, a
/// <c>SECURITY DEFINER</c> function owned by the migrator. The application role has no <c>DELETE</c> on the table and
/// cannot choose the cut-off: the period is fixed in the function, so a compromised application cannot shorten it to
/// erase its traces. Deletes in batches so one run never holds a long transaction.
/// </summary>
internal sealed partial class AuditRetentionJob(AppDbContext db, ILogger<AuditRetentionJob> logger)
{
    public const string JobId = "audit-retention";

    /// <summary>Entries deleted per call of the function.</summary>
    internal const int BatchSize = 5_000;

    /// <summary>A run stops after this many batches; the rest waits for the next day.</summary>
    private const int MaxBatchesPerRun = 200;

    /// <returns>The number of deleted entries.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Audit: delete entries past the retention period")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await db.Database
                .SqlQuery<int>($"SELECT audit.purge_expired({BatchSize}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            LogDeleted(logger, total);
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} audit entries past the retention period")]
    private static partial void LogDeleted(ILogger logger, int count);
}
