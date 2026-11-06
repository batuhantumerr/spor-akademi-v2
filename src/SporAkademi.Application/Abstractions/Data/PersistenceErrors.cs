using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions.Data;

public static class PersistenceErrors
{
    /// <summary>Someone else changed the record since it was read (API §1.7).</summary>
    public static readonly Error ConcurrencyConflict =
        Error.Conflict("Concurrency.Conflict", "The record was changed by someone else.");

    /// <summary>A row lock could not be acquired within <c>lock_timeout</c> (ADR-0005).</summary>
    public static readonly Error LockTimeout = Error.Conflict("Lock.Timeout", "Could not acquire a lock in time.");
}
