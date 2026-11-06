using SporAkademi.Domain.Groups;

namespace SporAkademi.Application.Attendance.Contracts;

/// <summary>Attendance records of one session date by status.</summary>
public sealed record DailyAttendance(DateOnly Date, int Present, int Late, int Absent, int Excused);

/// <summary>A stored session that has at least one attendance record.</summary>
public sealed record RecordedSession(GroupId GroupId, TimeOnly StartTime);

/// <summary>Read contract of the Attendance module (used by the dashboard).</summary>
public interface IAttendanceSummary
{
    /// <summary>Record counts per session date between the two dates (inclusive); dates without records are omitted.</summary>
    Task<IReadOnlyList<DailyAttendance>> CountByDayAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>Sessions on <paramref name="sessionDate"/> for which attendance has been recorded.</summary>
    Task<IReadOnlyList<RecordedSession>> RecordedSessionsOnAsync(DateOnly sessionDate, CancellationToken cancellationToken);
}
