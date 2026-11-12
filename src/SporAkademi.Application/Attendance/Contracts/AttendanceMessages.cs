using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Domain.Attendance;

namespace SporAkademi.Application.Attendance.Contracts;

/// <summary>
/// A student's attendance was set or changed in a session (P3-08, DEC-397): one message per student whose status really
/// changed, so a retry only repeats that student's notice. Ids, the session date and the status only (NFR-PRV-03).
/// </summary>
[OutboxMessage("attendance.attendance-recorded.v1")]
public sealed record AttendanceRecordedMessage(Guid SessionId, Guid GroupId, DateOnly Date, Guid StudentId, AttendanceStatus Status) : IOutboxMessage;
