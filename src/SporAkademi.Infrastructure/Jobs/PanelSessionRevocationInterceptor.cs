using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// Ends a user's job panel session whenever one of their sign-in sessions ends (DEC-332): logout, password change,
/// password reset, deactivation and refresh token reuse all revoke a refresh token without replacing it. A rotation
/// (normal refresh) revokes the old token but names its successor, so it does not count. The panel session is ended
/// before the save commits; if the save then fails the admin only has to open the panel again.
/// </summary>
internal sealed class PanelSessionRevocationInterceptor(JobsDashboardSessions sessions) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        EndPanelSessions(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        EndPanelSessions(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void EndPanelSessions(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var users = context.ChangeTracker.Entries<RefreshToken>()
            .Where(e => e.State == EntityState.Modified
                && e.OriginalValues[nameof(RefreshToken.RevokedAt)] is null
                && e.Entity.RevokedAt is not null
                && e.Entity.ReplacedByTokenId is null)
            .Select(e => e.Entity.UserId.Value)
            .Distinct();

        foreach (var user in users)
        {
            sessions.End(user);
        }
    }
}
