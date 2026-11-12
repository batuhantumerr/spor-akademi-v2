using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Application.Abstractions.Realtime;

/// <summary>Event types the clients understand (P3-08, DEC-397).</summary>
public static class RealtimeEventTypes
{
    public const string AnnouncementPublished = "announcement.published";
    public const string AttendanceRecorded = "attendance.recorded";
    public const string PaymentRecorded = "payment.recorded";
}

/// <summary>
/// What a connected client receives (DEC-397): the event type and ids, plus a session date and status for attendance.
/// Never names, amounts or free text; clients read the details again through the REST API, which applies every
/// authorization rule, so a notice can never show more than the recipient may already see.
/// </summary>
public sealed record RealtimeEvent(string Type, Guid Id, Guid? GroupId = null, Guid? StudentId = null, DateOnly? Date = null, string? Status = null);

/// <summary>
/// Pushes events to connected clients (P3-08). Called only from outbox handlers, so nothing is sent for a change that did
/// not commit (DEC-396). Recipients are always of the given school; a school's message never reaches another school.
/// </summary>
public interface IRealtimeNotifier
{
    Task NotifyUsersAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, RealtimeEvent realtimeEvent, CancellationToken cancellationToken);

    Task NotifyRoleAsync(Guid tenantId, Role role, RealtimeEvent realtimeEvent, CancellationToken cancellationToken);
}
