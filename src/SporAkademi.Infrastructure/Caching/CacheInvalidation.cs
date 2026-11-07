using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Caching;

/// <summary>
/// Automatic cache invalidation (DEC-313). Every save records which schools' areas it changed, from the change
/// tracker, so no handler can forget it; the tags are removed once the data is durable: after the transaction
/// commits (unit of work), or right after a save that ran without an explicit transaction. A rollback removes them
/// too, which is only an extra miss but drops anything a factory read inside the failed transaction. The removal
/// runs before the response is written, so the writer's next read is fresh.
/// </summary>
internal sealed class CacheInvalidationInterceptor(ICurrentUser currentUser, CacheTagInvalidator invalidator)
    : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushIfOutsideTransactionAsync(eventData).AsTask().GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await FlushIfOutsideTransactionAsync(eventData);
        return result;
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) =>
        FlushIfOutsideTransactionAsync(eventData).AsTask().GetAwaiter().GetResult();

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default) =>
        FlushIfOutsideTransactionAsync(eventData).AsTask();

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        FlushAsync().AsTask().GetAwaiter().GetResult();

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        FlushAsync().AsTask();

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        FlushAsync().AsTask().GetAwaiter().GetResult();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        FlushAsync().AsTask();

    /// <summary>
    /// The tags a changed entity invalidates; internal for the unit tests. <paramref name="fallbackTenant"/> is used
    /// only for entities that are neither a school nor tenant-scoped: the entity's own <c>TenantId</c> column when
    /// it has one, otherwise the request's school.
    /// </summary>
    internal static IEnumerable<string> TagsFor(object entity, Guid? fallbackTenant)
    {
        switch (entity)
        {
            case School school:
                yield return TenantCacheKey.AreaTag(school.Id, CacheArea.School);
                yield return TenantCacheKey.AreaTag(school.Id, CacheArea.Dashboard);
                break;
            case ITenantScoped scoped:
                yield return TenantCacheKey.AreaTag(scoped.TenantId, CacheArea.Dashboard);
                if (AreaOf(scoped) is { } area)
                {
                    yield return TenantCacheKey.AreaTag(scoped.TenantId, area);
                }

                break;
            default:
                // A child entity (e.g. a group fee) belongs to its own tenant column's school or, without one, to the
                // request's school (the tenant write guard enforces that), so that school's dashboard is stale.
                // Known gap (DEC-313): a Platform Admin request has no school, so a tenant-less child row it writes
                // invalidates nothing; the dashboard's 1-minute lifetime bounds the staleness.
                if (fallbackTenant is { } tenant && tenant != Guid.Empty)
                {
                    yield return TenantCacheKey.AreaTag(SchoolId.From(tenant), CacheArea.Dashboard);
                }

                break;
        }
    }

    private static CacheArea? AreaOf(ITenantScoped entity) => entity switch
    {
        Sport => CacheArea.Sports,
        Facility => CacheArea.Facilities,
        _ => null,
    };

    private void Collect(DbContextEventData eventData)
    {
        if (eventData.Context is not { } context)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                _pending.UnionWith(TagsFor(entry.Entity, TenantColumnOf(entry) ?? currentUser.TenantId));
            }
        }
    }

    /// <summary>The value of a mapped <c>TenantId</c> property, for entities that carry one without being <see cref="ITenantScoped"/>.</summary>
    private static Guid? TenantColumnOf(EntityEntry entry)
    {
        if (entry.Metadata.FindProperty(nameof(ITenantScoped.TenantId)) is null)
        {
            return null;
        }

        return entry.Property(nameof(ITenantScoped.TenantId)).CurrentValue switch
        {
            SchoolId school => school.Value,
            Guid id => id,
            _ => null,
        };
    }

    private ValueTask FlushIfOutsideTransactionAsync(DbContextEventData eventData) =>
        eventData.Context?.Database.CurrentTransaction is null ? FlushAsync() : ValueTask.CompletedTask;

    private ValueTask FlushAsync()
    {
        if (_pending.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var tags = _pending.ToArray();
        _pending.Clear();
        return invalidator.RemoveAsync(tags);
    }
}

/// <summary>
/// Removes tags from both cache levels; a failure is logged (tag count only) and never fails the write that already
/// committed. An invalidation that cannot reach the L2 (Valkey down, or skipped after a failure) is counted and
/// logged: this instance's L1 is still invalidated, but other instances may read the old L2 entry until it expires
/// (at most 30 min, DEC-315).
/// </summary>
internal sealed partial class CacheTagInvalidator(HybridCache cache, ResilientDistributedCache distributed, ILogger<CacheTagInvalidator> logger)
{
    private long _droppedDistributedInvalidations;

    /// <summary>Invalidations since start that did not reach the L2.</summary>
    public long DroppedDistributedInvalidations => Interlocked.Read(ref _droppedDistributedInvalidations);

    public async ValueTask RemoveAsync(IReadOnlyCollection<string> tags)
    {
        try
        {
            // Not the request's token: the data is already committed, so the entries must go even if the caller left.
            await cache.RemoveByTagAsync(tags, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogInvalidationFailed(logger, exception.GetType().Name, tags.Count);
        }

        // The L2 swallows its own failures, so check afterwards whether it was (or just became) unavailable.
        if (distributed.IsConfigured && !distributed.IsAvailable)
        {
            var dropped = Interlocked.Increment(ref _droppedDistributedInvalidations);
            LogDistributedInvalidationDropped(logger, tags.Count, dropped);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache invalidation of {TagCount} tags failed ({ErrorType}); entries expire with their lifetime")]
    private static partial void LogInvalidationFailed(ILogger logger, string errorType, int tagCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache invalidation of {TagCount} tags did not reach the distributed cache ({DroppedTotal} since start); other instances may serve old entries until they expire")]
    private static partial void LogDistributedInvalidationDropped(ILogger logger, int tagCount, long droppedTotal);
}
