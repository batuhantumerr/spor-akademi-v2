using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Attendance;

/// <summary>
/// One line of a session's attendance list (US-19): what the coach needs to take attendance and nothing
/// else (DEC-163, NFR-PRV-02). <see cref="Status"/> is null until recorded.
/// </summary>
public sealed record SessionStudentDto(Guid StudentId, string FirstName, string LastName, AttendanceStatus? Status);

/// <summary>A session's attendance list; <c>sessionKey</c> is <c>YYYY-MM-DDTHH:mm</c>, times <c>HH:mm</c>.</summary>
public sealed record SessionAttendanceDto(
    Guid GroupId,
    string SessionKey,
    DateOnly Date,
    string Start,
    string End,
    bool IsRecorded,
    IReadOnlyList<SessionStudentDto> Students);

/// <summary>One session in a student's history; <see cref="Status"/> is null when nothing was recorded.</summary>
public sealed record StudentSessionDto(
    Guid GroupId,
    string GroupName,
    string SessionKey,
    DateOnly Date,
    string Start,
    string End,
    AttendanceStatus? Status);

/// <summary>
/// Counts over the listed sessions. <see cref="Rate"/> is (present + late) / (present + late + absent),
/// rounded to four decimals; excused and unrecorded sessions are left out, null when nothing counts (DEC-174).
/// </summary>
public sealed record AttendanceSummaryDto(int Sessions, int Present, int Late, int Absent, int Excused, int NotRecorded, decimal? Rate);

public sealed record StudentAttendanceDto(
    Guid StudentId,
    string FirstName,
    string LastName,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<StudentSessionDto> Sessions,
    AttendanceSummaryDto Summary);

public sealed record MonthSessionDto(string SessionKey, DateOnly Date, string Start, string End, bool IsRecorded);

/// <summary>A student's row of the month table; <see cref="Statuses"/> lines up with the month's sessions.</summary>
public sealed record MonthStudentRowDto(Guid StudentId, string FirstName, string LastName, IReadOnlyList<AttendanceStatus?> Statuses);

/// <summary>Sessions × students of a group for a month (US-21); <c>month</c> is <c>YYYY-MM</c>.</summary>
public sealed record GroupAttendanceMonthDto(
    Guid GroupId,
    string Month,
    IReadOnlyList<MonthSessionDto> Sessions,
    IReadOnlyList<MonthStudentRowDto> Students);

/// <summary>A recorded status of one student in a stored session.</summary>
public sealed record StudentSessionRecord(GroupId GroupId, DateOnly Date, TimeOnly Start, TimeOnly End, AttendanceStatus Status);

/// <summary>
/// Serializes saves of one session (DEC-173): concurrent saves of the same group, date and start time wait
/// for each other instead of conflicting, so each reads the attendance the previous one committed. Held
/// until the transaction ends; waits end after <c>lock_timeout</c> with <c>Lock.Timeout</c>.
/// </summary>
public interface ISessionLocks
{
    Task LockAsync(GroupId groupId, SessionKey key, CancellationToken cancellationToken);
}

/// <summary>Sessions of the current school (tenant filter applies).</summary>
public interface ISessionRepository
{
    /// <summary>Tracked, with its records, for saving attendance.</summary>
    Task<Session?> GetAsync(GroupId groupId, SessionKey key, CancellationToken cancellationToken);

    void Add(Session session);

    /// <summary>
    /// Makes the save update the session row with its version check even when only records change, so
    /// two concurrent saves of one session cannot both pass (DEC-173).
    /// </summary>
    void Touch(Session session);

    /// <summary>Untracked, with its records.</summary>
    Task<Session?> FindAsync(GroupId groupId, SessionKey key, CancellationToken cancellationToken);

    /// <summary>Untracked stored sessions of the group between the dates (inclusive), with records.</summary>
    Task<IReadOnlyList<Session>> ListOfGroupAsync(GroupId groupId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>The student's recorded statuses between the dates (inclusive), in any group.</summary>
    Task<IReadOnlyList<StudentSessionRecord>> ListOfStudentAsync(StudentId studentId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}
