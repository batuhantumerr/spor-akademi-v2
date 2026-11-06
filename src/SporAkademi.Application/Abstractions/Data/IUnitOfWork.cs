using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions.Data;

/// <summary>
/// Runs an operation in one database transaction. On success the changes are saved (domain events are
/// handled before commit, ADR-0009) and committed; on a failed <see cref="Result"/> or an exception
/// everything is rolled back.
/// </summary>
public interface IUnitOfWork
{
    Task<Result<T>> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(operation, commitOnFailure: false, cancellationToken);

    /// <summary>
    /// With <paramref name="commitOnFailure"/> a failed result is saved and committed too, for operations
    /// whose failure itself changes state (e.g. a wrong password counts towards the lockout).
    /// </summary>
    Task<Result<T>> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        bool commitOnFailure,
        CancellationToken cancellationToken);
}
