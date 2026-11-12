using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Realtime;

namespace SporAkademi.Infrastructure.Notifications;

/// <summary>
/// Hosts without a SignalR hub (the demo seeder, tools) deliver realtime messages to nobody; the API replaces this with
/// its hub notifier (P3-08). The message still counts as delivered: a client that was not connected reads the REST API
/// when it connects (DEC-399).
/// </summary>
internal sealed class NoRealtimeNotifier : IRealtimeNotifier
{
    public Task NotifyUsersAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task NotifyRoleAsync(Guid tenantId, Role role, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
