using System.Data.Common;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>The tags a set of changed entities invalidates; internal for the unit tests.</summary>
    internal static IEnumerable<string> TagsFor(object entity, Guid? requestTenant)
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
                // A child entity without its own tenant (e.g. a group fee) belongs to the request's school,
                // which the tenant write guard enforces, so that school's dashboard is stale.
                if (requestTenant is { } tenant && tenant != Guid.Empty)
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
                _pending.UnionWith(TagsFor(entry.Entity, currentUser.TenantId));
            }
        }
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

/// <summary>Removes tags from both cache levels; a failure is logged (tag count only) and never fails the write that already committed.</summary>
internal sealed partial class CacheTagInvalidator(HybridCache cache, ILogger<CacheTagInvalidator> logger)
{
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
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache invalidation of {TagCount} tags failed ({ErrorType}); entries expire with their lifetime")]
    private static partial void LogInvalidationFailed(ILogger logger, string errorType, int tagCount);
}
