using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// Layer 2 of ADR-0003: a request bound to a school can only add, change or delete that school's rows,
/// and a row's tenant never changes. Violations are programmer errors or attacks, so they throw.
/// </summary>
internal sealed class TenantWriteGuardInterceptor(ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var tenant = currentUser.TenantId;
        if (currentUser.IsAuthenticated && currentUser.Role != Role.PlatformAdmin && tenant is null)
        {
            throw new TenantIsolationException("A school user without a tenant cannot write.");
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.State == EntityState.Modified
                && !Equals(entry.OriginalValues[nameof(ITenantScoped.TenantId)], entry.CurrentValues[nameof(ITenantScoped.TenantId)]))
            {
                throw new TenantIsolationException($"The tenant of {entry.Metadata.ClrType.Name} cannot change.");
            }

            // Platform-level work (Platform Admin or system, no tenant) is deliberate and owned by its module.
            if (tenant is { } current && entry.Entity.TenantId.Value != current)
            {
                throw new TenantIsolationException($"Cannot write {entry.Metadata.ClrType.Name} of another school.");
            }
        }
    }
}

public sealed class TenantIsolationException : InvalidOperationException
{
    public TenantIsolationException()
    {
    }

    public TenantIsolationException(string message)
        : base(message)
    {
    }

    public TenantIsolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
