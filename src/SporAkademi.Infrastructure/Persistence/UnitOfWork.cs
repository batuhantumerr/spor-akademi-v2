using Microsoft.EntityFrameworkCore;
using Npgsql;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// One transaction per command. After the operation succeeds: save, hand raised domain events to their
/// handlers (which may change more data and raise more events), save again, and commit (ADR-0009).
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
            // Nested call: the outer unit of work saves and commits.
            return await operation(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            if (result.IsFailure)
            {
                await RollbackAsync(transaction);
                return result;
            }

            var saved = await SaveAndDispatchEventsAsync(cancellationToken);
            if (saved.IsFailure)
            {
                await RollbackAsync(transaction);
                return Result.Failure<T>(saved.Error);
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            return Result.Failure<T>(PersistenceErrors.ConcurrencyConflict);
        }
        catch (Exception exception) when (IsLockTimeout(exception))
        {
            await RollbackAsync(transaction);
            return Result.Failure<T>(PersistenceErrors.LockTimeout);
        }
    }

    private async Task<Result> SaveAndDispatchEventsAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxEventRounds; round++)
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            var events = dbContext.ChangeTracker.Entries<IAggregateRoot>()
                .SelectMany(entry =>
                {
                    var raised = entry.Entity.DomainEvents.ToList();
                    entry.Entity.ClearDomainEvents();
                    return raised;
                })
                .ToList();

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

    private async Task RollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();
    }

    private static bool IsLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: LockNotAvailable }
        || exception.InnerException is PostgresException { SqlState: LockNotAvailable };
}
