using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SporAkademi.Api.Authentication;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Realtime;

namespace SporAkademi.Api.Realtime;

/// <summary>What the server calls on a client; the JavaScript clients listen to <c>notify</c>.</summary>
public interface INotificationClient
{
    Task Notify(RealtimeEvent realtimeEvent);
}

/// <summary>
/// Hub group names (DEC-398). Every name starts with the school, so no school's message can reach another school's
/// connection; the server alone puts a connection into groups, from its validated token.
/// </summary>
internal static class RealtimeGroups
{
    public static string User(Guid tenantId, Guid userId) => $"t:{tenantId:N}:u:{userId:N}";

    public static string Role(Guid tenantId, Role role) => $"t:{tenantId:N}:r:{role}";
}

/// <summary>
/// P3-08 (DEC-396 – DEC-400): server-to-client only. School Admins, coaches and parents of a school connect with their
/// access token; the hub has no client-callable methods, so a client can neither join a group nor send anything.
/// Groups are assigned here from the token (school + role, school + user); who receives an event is decided by the
/// outbox handler when it is delivered. A user may hold <see cref="MaxConnectionsPerUser"/> connections per instance.
/// </summary>
[Authorize(Roles = $"{nameof(Role.SchoolAdmin)},{nameof(Role.Coach)},{nameof(Role.Parent)}")]
internal sealed class NotificationHub : Hub<INotificationClient>
{
    public const string Path = "/hubs/notifications";

    /// <summary>Tabs and devices of one person; more is a runaway client.</summary>
    public const int MaxConnectionsPerUser = 10;

    private static readonly ConcurrentDictionary<Guid, int> ConnectionsPerUser = new();

    private const string CountedKey = "counted-user";

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User;
        var role = Enum.TryParse<Role>(principal?.FindFirst("role")?.Value, out var parsed) ? parsed : (Role?)null;
        if (!Guid.TryParse(principal?.FindFirst("sub")?.Value, out var userId)
            || !Guid.TryParse(principal?.FindFirst(HttpCurrentUser.TenantIdClaim)?.Value, out var tenantId)
            || role is null or Role.PlatformAdmin)
        {
            Context.Abort();
            return;
        }

        if (ConnectionsPerUser.AddOrUpdate(userId, 1, (_, count) => count + 1) > MaxConnectionsPerUser)
        {
            Release(userId);
            Context.Abort();
            return;
        }

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(tenantId, userId));
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Role(tenantId, role.Value));
            await base.OnConnectedAsync();
        }
        catch
        {
            // E.g. the backplane is down: SignalR then never calls OnDisconnectedAsync for this connection, so the
            // slot is given back here, otherwise ten failures would lock the user out until a restart.
            Release(userId);
            throw;
        }

        Context.Items[CountedKey] = userId;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(CountedKey, out var counted) && counted is Guid userId)
        {
            Release(userId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    private static void Release(Guid userId)
    {
        while (ConnectionsPerUser.TryGetValue(userId, out var count))
        {
            var updated = count <= 1
                ? ConnectionsPerUser.TryRemove(new KeyValuePair<Guid, int>(userId, count))
                : ConnectionsPerUser.TryUpdate(userId, count - 1, count);
            if (updated)
            {
                return;
            }
        }
    }
}

/// <summary>Sends through the hub (and through the Valkey backplane to other instances when it is configured).</summary>
internal sealed class HubRealtimeNotifier(IHubContext<NotificationHub, INotificationClient> hub) : IRealtimeNotifier
{
    public Task NotifyUsersAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        return userIds.Count == 0
            ? Task.CompletedTask
            : hub.Clients.Groups([.. userIds.Distinct().Select(u => RealtimeGroups.User(tenantId, u))]).Notify(realtimeEvent);
    }

    public Task NotifyRoleAsync(Guid tenantId, Role role, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeGroups.Role(tenantId, role)).Notify(realtimeEvent);
}
