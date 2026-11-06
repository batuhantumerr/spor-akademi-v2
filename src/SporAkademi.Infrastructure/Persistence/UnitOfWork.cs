using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// One transaction per command. After the operation succeeds, raised domain events are collected,
/// changes are saved, and the events go to their consumers (which may change more data and raise more
/// events); this repeats until no events remain, then the transaction commits (ADR-0009).
/// </summary>
internal sealed class UnitOfWork(AppDbContext dbContext, DomainEventDispatcher dispatcher) : IUnitOfWork
{
    private const int MaxEventRounds = 10;
    private const string LockNotAvailable = "55P03";

    public async Task<Result<T>> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (dbContext.Database.CurrentTransaction is not null)
        {
            // Nested call: the outer unit of work saves and commits. The caller must propagate a
            // failed result, otherwise the outer operation commits whatever it has tracked.
            return await operation(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            // ADR-0005: lock waits fail after 5 s instead of queueing requests indefinitely.
            await dbContext.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);

            var result = await operation(cancellationToken);
            if (result.IsFailure)
            {
                return result;
            }

            var saved = await SaveAndDispatchEventsAsync(cancellationToken);
            if (saved.IsFailure)
            {
                return Result.Failure<T>(saved.Error);
            }

            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<T>(PersistenceErrors.ConcurrencyConflict);
        }
        catch (Exception exception) when (IsLockTimeout(exception))
        {
            return Result.Failure<T>(PersistenceErrors.LockTimeout);
        }
        finally
        {
            if (!committed)
            {
                await TryRollbackAsync(transaction);
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task<Result> SaveAndDispatchEventsAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxEventRounds; round++)
        {
            // Collect before saving: deleted aggregates are detached by SaveChanges.
            var events = dbContext.ChangeTracker.Entries<IAggregateRoot>()
                .SelectMany(entry =>
                {
                    var raised = entry.Entity.DomainEvents.ToList();
                    entry.Entity.ClearDomainEvents();
                    return raised;
                })
                .ToList();

            await dbContext.SaveChangesAsync(cancellationToken);

            if (events.Count == 0)
            {
                return Result.Success();
            }

            foreach (var domainEvent in events)
            {
                var handled = await dispatcher.DispatchAsync(domainEvent, cancellationToken);
                if (handled.IsFailure)
                {
                    return handled;
                }
            }
        }

        throw new InvalidOperationException($"Domain events still pending after {MaxEventRounds} rounds; check for an event cycle.");
    }

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // A broken connection must not hide the original error; the server discards the transaction.
        }
    }

    internal static bool IsLockTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: LockNotAvailable })
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsLockTimeout))
            {
                return true;
            }
        }

        return false;
    }
}
