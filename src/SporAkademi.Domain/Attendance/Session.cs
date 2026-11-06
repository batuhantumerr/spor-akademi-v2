using System.Globalization;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Attendance;

public readonly record struct SessionId(Guid Value) : IEntityId<SessionId>
{
    public static SessionId From(Guid value) => new(value);
}

public enum AttendanceStatus
{
    Present,
    Absent,
    Late,
    Excused,
}

/// <summary>One student's status in a save request.</summary>
public sealed record AttendanceMark(StudentId StudentId, AttendanceStatus Status);

/// <summary>Who saves attendance; a School Admin may edit at any time, a coach only within the window (DEC-014).</summary>
public sealed record Recorder(UserId UserId, bool IsSchoolAdmin);

/// <summary>
/// Addresses a session that may not be stored yet (DEC-016): its group's date and start time, written
/// <c>YYYY-MM-DDTHH:mm</c> in the school's time zone (appendix D §2.9).
/// </summary>
public readonly record struct SessionKey(DateOnly Date, TimeOnly Start)
{
    private const string Format = "yyyy-MM-dd'T'HH:mm";

    public static Result<SessionKey> Parse(string? text)
    {
        if (text is { Length: 16 }
            && DateTime.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value.Year is >= YearMonth.MinYear and <= YearMonth.MaxYear)
        {
            return Result.Success(new SessionKey(DateOnly.FromDateTime(value), TimeOnly.FromDateTime(value)));
        }

        return Result.Failure<SessionKey>(AttendanceErrors.InvalidSessionKey);
    }

    public override string ToString() => Date.ToDateTime(Start).ToString(Format, CultureInfo.InvariantCulture);
}

/// <summary>
/// A held training of a group (appendix B 4.9). Created lazily when attendance is first saved; the time
/// is a snapshot of the schedule, so later schedule changes never rewrite history. Unique per group,
/// date and start time. Times are local to the school.
/// </summary>
public sealed class Session : AggregateRoot<SessionId>, ITenantScoped
{
    /// <summary>A coach may change attendance until this long after the session ends (DEC-014, US-20).</summary>
    public static readonly TimeSpan CoachEditWindow = TimeSpan.FromHours(48);

    private readonly List<AttendanceRecord> _records = [];
    private readonly List<AttendanceChange> _changes = [];

    private Session(SessionId id, SchoolId tenantId, GroupId groupId, DateOnly date, TimeRange time)
        : base(id)
    {
        TenantId = tenantId;
        GroupId = groupId;
        Date = date;
        StartTime = time.Start;
        EndTime = time.End;
    }

    // For EF Core materialization only.
    private Session()
        : base(default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public GroupId GroupId { get; private set; }

    public DateOnly Date { get; private set; }

    public TimeOnly StartTime { get; private set; }

    public TimeOnly EndTime { get; private set; }

    public SessionKey Key => new(Date, StartTime);

    public DateTime StartsAt => Date.ToDateTime(StartTime);

    public DateTime EndsAt => Date.ToDateTime(EndTime);

    public IReadOnlyList<AttendanceRecord> Records => _records.AsReadOnly();

    public IReadOnlyList<AttendanceChange> Changes => _changes.AsReadOnly();

    /// <summary>Opens the session for a planned training; <paramref name="time"/> is copied from the schedule.</summary>
    public static Session Open(SchoolId tenantId, GroupId groupId, DateOnly date, TimeRange time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new Session(EntityId.New<SessionId>(), tenantId, groupId, date, time);
    }

    /// <summary>
    /// Whether <paramref name="recorder"/> may save now: never before the session starts (US-19); a coach
    /// only until <see cref="CoachEditWindow"/> after it ends, a School Admin at any time (US-20).
    /// </summary>
    public Result CheckCanSave(Recorder recorder, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        if (StartsAt > localNow)
        {
            return Result.Failure(AttendanceErrors.SessionInFuture);
        }

        return !recorder.IsSchoolAdmin && localNow > EndsAt + CoachEditWindow
            ? Result.Failure(AttendanceErrors.EditWindowExpired)
            : Result.Success();
    }

    /// <summary>
    /// Records the given statuses; students not mentioned keep theirs. Saving the same status again
    /// changes nothing (idempotent PUT); a different status is kept as a change with who and when (US-20).
    /// </summary>
    /// <param name="localNow">Now in the school's time zone.</param>
    public Result Save(IReadOnlyCollection<AttendanceMark> marks, Recorder recorder, DateTime localNow, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(recorder);

        if (marks.Select(m => m.StudentId).Distinct().Count() != marks.Count || marks.Any(m => !Enum.IsDefined(m.Status)))
        {
            return Result.Failure(CommonErrors.FieldInvalid);
        }

        var allowed = CheckCanSave(recorder, localNow);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        foreach (var mark in marks)
        {
            var record = _records.Find(r => r.StudentId == mark.StudentId);
            if (record is null)
            {
                _records.Add(new AttendanceRecord(TenantId, Id, mark.StudentId, mark.Status, recorder.UserId, at));
            }
            else if (record.Status != mark.Status)
            {
                _changes.Add(new AttendanceChange(TenantId, Id, mark.StudentId, record.Status, mark.Status, recorder.UserId, at));
                record.Change(mark.Status, recorder.UserId, at);
            }
        }

        return Result.Success();
    }
}

/// <summary>A student's current status in a session; one per student and session.</summary>
public sealed class AttendanceRecord : ITenantScoped
{
    internal AttendanceRecord(SchoolId tenantId, SessionId sessionId, StudentId studentId, AttendanceStatus status, UserId recordedBy, DateTimeOffset recordedAt)
    {
        TenantId = tenantId;
        SessionId = sessionId;
        StudentId = studentId;
        Status = status;
        RecordedBy = recordedBy;
        RecordedAt = recordedAt;
    }

    // For EF Core materialization only.
    private AttendanceRecord()
    {
    }

    public SchoolId TenantId { get; private set; }

    public SessionId SessionId { get; private set; }

    public StudentId StudentId { get; private set; }

    public AttendanceStatus Status { get; private set; }

    /// <summary>Who set the current status, and when.</summary>
    public UserId RecordedBy { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    internal void Change(AttendanceStatus status, UserId by, DateTimeOffset at)
    {
        Status = status;
        RecordedBy = by;
        RecordedAt = at;
    }
}

/// <summary>History of a correction (US-20): the status before and after, who changed it and when.</summary>
public sealed class AttendanceChange : ITenantScoped
{
    internal AttendanceChange(
        SchoolId tenantId,
        SessionId sessionId,
        StudentId studentId,
        AttendanceStatus previousStatus,
        AttendanceStatus newStatus,
        UserId changedBy,
        DateTimeOffset changedAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        SessionId = sessionId;
        StudentId = studentId;
        PreviousStatus = previousStatus;
        NewStatus = newStatus;
        ChangedBy = changedBy;
        ChangedAt = changedAt;
    }

    // For EF Core materialization only.
    private AttendanceChange()
    {
    }

    public Guid Id { get; private set; }

    public SchoolId TenantId { get; private set; }

    public SessionId SessionId { get; private set; }

    public StudentId StudentId { get; private set; }

    public AttendanceStatus PreviousStatus { get; private set; }

    public AttendanceStatus NewStatus { get; private set; }

    public UserId ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }
}

public static class AttendanceErrors
{
    public static readonly Error SessionInFuture = Error.BusinessRule("Attendance.SessionInFuture", "The session has not started yet.");
    public static readonly Error SessionNotInSchedule = Error.NotFound("Attendance.SessionNotInSchedule", "The group has no session at this date and time.");
    public static readonly Error InvalidSessionKey = Error.Validation("Attendance.InvalidSessionKey", "The session date and time are not valid.");
    public static readonly Error EditWindowExpired = Error.Forbidden("Attendance.EditWindowExpired", "Only a School Admin can change attendance older than 48 hours.");

    /// <summary>Argument <c>studentId</c>: the first student not enrolled on the session date.</summary>
    public static Error StudentNotEnrolled(Guid studentId) =>
        Error.BusinessRule("Attendance.StudentNotEnrolled", "The student is not enrolled in the group on the session date.")
            .WithArgument("studentId", studentId);
}
