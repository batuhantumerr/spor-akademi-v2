using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>Stamps CreatedAt/By and UpdatedAt/By from <see cref="TimeProvider"/> and the current user.</summary>
internal sealed class AuditingInterceptor(TimeProvider timeProvider, ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not null)
        {
            Stamp(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.Context is not null)
        {
            Stamp(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    private void Stamp(DbContext context)
    {
        var now = timeProvider.GetUtcNow();
        var actor = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Metadata.FindProperty(AuditColumns.CreatedAt) is null)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                entry.Property(AuditColumns.CreatedAt).CurrentValue = now;
                entry.Property(AuditColumns.CreatedBy).CurrentValue = actor;
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Property(AuditColumns.UpdatedAt).CurrentValue = now;
                entry.Property(AuditColumns.UpdatedBy).CurrentValue = actor;
            }
        }
    }
}
