using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Persistence;

internal static class TenantFilters
{
    private static readonly MethodInfo ApplyMethod =
        typeof(TenantFilters).GetMethod(nameof(Apply), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Layer 1 of ADR-0003: every query on a tenant-scoped entity only sees the current school's rows.
    /// Without a tenant (Platform Admin, anonymous) the filter matches nothing: deny by default.
    /// </summary>
    public static void AddTenantQueryFilters(this ModelBuilder modelBuilder, AppDbContext context)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => t.BaseType is null && typeof(ITenantScoped).IsAssignableFrom(t.ClrType)).ToList())
        {
            ApplyMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [modelBuilder, context]);
        }
    }

    private static void Apply<TEntity>(ModelBuilder modelBuilder, AppDbContext context)
        where TEntity : class, ITenantScoped =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == context.CurrentTenantId);
}

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

            // Platform-level work (no tenant in the token) is deliberate and audited by its own module.
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
