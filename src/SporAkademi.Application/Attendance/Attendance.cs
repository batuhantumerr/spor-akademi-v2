using System.Globalization;
using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Attendance;

public sealed record AttendanceMarkInput(Guid StudentId, AttendanceStatus Status);

/// <summary>Saves statuses for the listed students (US-19, US-20); others keep theirs. Idempotent.</summary>
public sealed record SaveAttendanceCommand(GroupId GroupId, string SessionKey, IReadOnlyList<AttendanceMarkInput>? Records) : ICommand<Unit>;

public sealed record GetSessionAttendanceQuery(GroupId GroupId, string SessionKey) : IQuery<SessionAttendanceDto>;

public sealed record GetStudentAttendanceQuery(StudentId StudentId, DateOnly? From, DateOnly? To) : IQuery<StudentAttendanceDto>
{
    /// <summary>A year of history at most per request.</summary>
    public const int MaxDays = 366;
}

public sealed record GetGroupAttendanceMonthQuery(GroupId GroupId, string? Month) : IQuery<GroupAttendanceMonthDto>;

internal sealed class SaveAttendanceValidator : AbstractValidator<SaveAttendanceCommand>
{
    public SaveAttendanceValidator()
    {
        RuleFor(c => c.Records).NotNull().WithErrorCode("Field.Required")
            .Must(r => r is null || r.Count <= Group.MaxCapacity).WithErrorCode("Field.Invalid");
        RuleForEach(c => c.Records).ChildRules(record =>
        {
            record.RuleFor(r => r.StudentId).NotEmpty().WithErrorCode("Field.Required");
            record.RuleFor(r => r.Status).IsInEnum().WithErrorCode("Field.Invalid");
        });
    }
}

internal sealed class GetStudentAttendanceValidator : AbstractValidator<GetStudentAttendanceQuery>
{
    public GetStudentAttendanceValidator()
    {
        RuleFor(q => q.From).NotNull().WithErrorCode("Field.Required");
        RuleFor(q => q.To).NotNull().WithErrorCode("Field.Required")
            .Must((q, to) => q.From is not { } from || to is not { } t || (t >= from && t.DayNumber - from.DayNumber < GetStudentAttendanceQuery.MaxDays))
            .WithErrorCode("Field.Invalid");
    }
}

internal sealed class GetGroupAttendanceMonthValidator : AbstractValidator<GetGroupAttendanceMonthQuery>
{
    public GetGroupAttendanceMonthValidator()
    {
        RuleFor(q => q.Month).NotEmpty().WithErrorCode("Field.Required");
        RuleFor(q => q.Month).Must(m => YearMonth.Parse(m!).IsSuccess).WithErrorCode(YearMonth.Invalid.Code)
            .When(q => !string.IsNullOrEmpty(q.Month));
    }
}

/// <summary>Who may see a group's attendance: a School Admin every group, a coach only own active groups (US-12, US-21).</summary>
internal static class AttendanceAccess
{
    public static async Task<GroupInfo?> FindVisibleGroupAsync(this IGroupDirectory groups, GroupId groupId, ICurrentUser currentUser, CancellationToken cancellationToken)
    {
        var group = await groups.FindAsync(groupId, cancellationToken);
        return group is not null && IsVisible(group, currentUser) ? group : null;
    }

    public static bool IsVisible(GroupInfo group, ICurrentUser currentUser) =>
        currentUser.Role != Role.Coach || (!group.IsArchived && currentUser.UserId is { } me && group.CoachId == me);

    /// <summary>The stored session, or the planned one the schedule has at that date and start time.</summary>
    public static TimeRange? PlannedTime(GroupInfo group, SessionKey key) =>
        group.PlannedBetween(key.Date, key.Date).FirstOrDefault(p => p.Time.Start == key.Start)?.Time;
}

internal static class AttendanceViews
{
    public static readonly StringComparer Turkish = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static IOrderedEnumerable<StudentInfo> SortByName(this IEnumerable<StudentInfo> students) =>
        students.OrderBy(s => s.LastName, Turkish).ThenBy(s => s.FirstName, Turkish).ThenBy(s => s.Id);

    /// <summary>Students enrolled on the session date plus anyone already recorded in it, with their status.</summary>
    public static async Task<SessionAttendanceDto> SessionListAsync(
        GroupInfo group,
        SessionKey key,
        TimeRange time,
        Session? session,
        IEnrollmentHistory enrollments,
        IStudentDirectory students,
        CancellationToken cancellationToken)
    {
        var groupId = GroupId.From(group.Id);
        var enrolled = (await enrollments.ListOfGroupAsync(groupId, key.Date, key.Date, cancellationToken))
            .Where(p => p.Covers(key.Date))
            .Select(p => p.StudentId.Value);
        var statuses = session?.Records.ToDictionary(r => r.StudentId.Value, r => r.Status) ?? [];
        var infos = await students.GetManyAsync([.. enrolled.Union(statuses.Keys)], cancellationToken);

        return new SessionAttendanceDto(
            group.Id,
            key.ToString(),
            key.Date,
            Format(time.Start),
            Format(time.End),
            session is not null,
            [.. infos.Values.SortByName().Select(s => new SessionStudentDto(s.Id, s.FirstName, s.LastName, statuses.TryGetValue(s.Id, out var status) ? status : null))]);
    }
}

internal sealed class GetSessionAttendanceHandler(
    ISessionRepository sessions,
    IGroupDirectory groups,
    IEnrollmentHistory enrollments,
    IStudentDirectory students,
    ICurrentUser currentUser) : IQueryHandler<GetSessionAttendanceQuery, SessionAttendanceDto>
{
    public async Task<Result<SessionAttendanceDto>> Handle(GetSessionAttendanceQuery query, CancellationToken cancellationToken)
    {
        var key = SessionKey.Parse(query.SessionKey);
        if (key.IsFailure)
        {
            return Result.Failure<SessionAttendanceDto>(key.Error);
        }

        var group = await groups.FindVisibleGroupAsync(query.GroupId, currentUser, cancellationToken);
        if (group is null)
        {
            return Result.Failure<SessionAttendanceDto>(GroupErrors.NotFound);
        }

        // A stored session keeps its snapshot time even if the schedule changed since (DEC-016).
        var session = await sessions.FindAsync(query.GroupId, key.Value, cancellationToken);
        var time = session is not null
            ? TimeRange.Create(session.StartTime, session.EndTime).Value
            : AttendanceAccess.PlannedTime(group, key.Value);
        if (time is null)
        {
            return Result.Failure<SessionAttendanceDto>(AttendanceErrors.SessionNotInSchedule);
        }

        return Result.Success(await AttendanceViews.SessionListAsync(group, key.Value, time, session, enrollments, students, cancellationToken));
    }
}

internal sealed class SaveAttendanceHandler(
    ISessionRepository sessions,
    ISessionLocks locks,
    IGroupDirectory groups,
    IEnrollmentHistory enrollments,
    ITenancyModule tenancy,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : ICommandHandler<SaveAttendanceCommand, Unit>
{
    public async Task<Result<Unit>> Handle(SaveAttendanceCommand command, CancellationToken cancellationToken)
    {
        var key = SessionKey.Parse(command.SessionKey);
        if (key.IsFailure)
        {
            return Result.Failure<Unit>(key.Error);
        }

        var group = await groups.FindVisibleGroupAsync(command.GroupId, currentUser, cancellationToken);
        if (group is null)
        {
            return Result.Failure<Unit>(GroupErrors.NotFound);
        }

        var tenant = currentUser.RequireTenant();
        // Before reading the session, so this save sees whatever a concurrent save committed (DEC-173).
        await locks.LockAsync(command.GroupId, key.Value, cancellationToken);
        var session = await sessions.GetAsync(command.GroupId, key.Value, cancellationToken);
        var isNew = session is null;
        if (session is null)
        {
            if (AttendanceAccess.PlannedTime(group, key.Value) is not { } planned)
            {
                return Result.Failure<Unit>(AttendanceErrors.SessionNotInSchedule);
            }

            session = Session.Open(tenant, command.GroupId, key.Value.Date, planned);
        }

        var recorder = new Recorder(
            UserId.From(currentUser.UserId ?? throw new InvalidOperationException("Attendance is saved by a signed-in user.")),
            currentUser.Role == Role.SchoolAdmin);
        var localNow = await tenancy.LocalNowAsync(tenant, cancellationToken);
        var allowed = session.CheckCanSave(recorder, localNow);
        if (allowed.IsFailure)
        {
            return Result.Failure<Unit>(allowed.Error);
        }

        // Only students enrolled on the session date; an existing record can always be corrected.
        var marks = command.Records!;
        var enrolled = (await enrollments.ListOfGroupAsync(command.GroupId, key.Value.Date, key.Value.Date, cancellationToken))
            .Where(p => p.Covers(key.Value.Date))
            .Select(p => p.StudentId.Value)
            .Concat(session.Records.Select(r => r.StudentId.Value))
            .ToHashSet();
        if (marks.FirstOrDefault(m => !enrolled.Contains(m.StudentId)) is { } stranger)
        {
            return Result.Failure<Unit>(AttendanceErrors.StudentNotEnrolled(stranger.StudentId));
        }

        var saved = session.Save(
            [.. marks.Select(m => new AttendanceMark(StudentId.From(m.StudentId), m.Status))],
            recorder,
            localNow,
            timeProvider.GetUtcNow());
        if (saved.IsFailure)
        {
            return Result.Failure<Unit>(saved.Error == CommonErrors.FieldInvalid ? ValidationErrors.ForField("records", saved.Error) : saved.Error);
        }

        if (isNew)
        {
            sessions.Add(session);
        }
        else
        {
            sessions.Touch(session);
        }

        return Result.Success(Unit.Value);
    }
}

/// <summary>
/// DEC-173 backstop: saves of one session are serialized by <see cref="ISessionLocks"/>; should a save still
/// meet a conflict on the session row or its unique key, it is run again against the committed state (the
/// PUT has no version and merges per student).
/// </summary>
public static class AttendanceConflicts
{
    public const int MaxAttempts = 3;

    public static async Task<Result<T>> RetryAsync<T>(Func<Task<Result<T>>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await operation();
        for (var attempt = 1; attempt < MaxAttempts && result.IsFailure && result.Error == PersistenceErrors.ConcurrencyConflict; attempt++)
        {
            result = await operation();
        }

        return result;
    }
}

internal sealed class GetStudentAttendanceHandler(
    ISessionRepository sessions,
    IGroupDirectory groups,
    IEnrollmentHistory enrollments,
    IStudentDirectory students,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<GetStudentAttendanceQuery, StudentAttendanceDto>
{
    public async Task<Result<StudentAttendanceDto>> Handle(GetStudentAttendanceQuery query, CancellationToken cancellationToken)
    {
        var (from, to) = (query.From!.Value, query.To!.Value);
        var student = await students.FindAsync(query.StudentId, cancellationToken);
        if (student is null)
        {
            return Result.Failure<StudentAttendanceDto>(StudentErrors.NotFound);
        }

        var periods = await enrollments.ListOfStudentAsync(query.StudentId, cancellationToken);
        var recorded = await sessions.ListOfStudentAsync(query.StudentId, from, to, cancellationToken);
        if (currentUser.Role == Role.Coach)
        {
            // A coach sees the student only through own groups (US-21); without one the student is "not found".
            var own = (await groups.ListActiveIdsOfCoachAsync(currentUser.UserId!.Value, cancellationToken)).ToHashSet();
            periods = [.. periods.Where(p => own.Contains(p.GroupId))];
            recorded = [.. recorded.Where(r => own.Contains(r.GroupId))];
            if (periods.Count == 0)
            {
                return Result.Failure<StudentAttendanceDto>(StudentErrors.NotFound);
            }
        }

        var groupInfos = await groups.GetManyAsync([.. periods.Select(p => p.GroupId.Value).Union(recorded.Select(r => r.GroupId.Value))], cancellationToken);
        var localNow = await tenancy.LocalNowAsync(currentUser.RequireTenant(), cancellationToken);

        // Planned sessions while enrolled that have started, then recorded sessions on top (they win).
        var rows = new Dictionary<(Guid Group, DateOnly Date, TimeOnly Start), StudentSessionDto>();
        foreach (var period in periods.Where(p => p.Overlaps(from, to)))
        {
            if (!groupInfos.TryGetValue(period.GroupId.Value, out var group))
            {
                continue;
            }

            var first = period.StartDate > from ? period.StartDate : from;
            var last = period.EndDate is { } end && end < to ? end : to;
            foreach (var planned in group.PlannedBetween(first, last).Where(p => p.Date.ToDateTime(p.Time.Start) <= localNow))
            {
                rows[(group.Id, planned.Date, planned.Time.Start)] = Row(group, planned.Date, planned.Time.Start, planned.Time.End, null);
            }
        }

        foreach (var record in recorded)
        {
            var name = groupInfos.TryGetValue(record.GroupId.Value, out var group) ? group.Name : string.Empty;
            rows[(record.GroupId.Value, record.Date, record.Start)] = new StudentSessionDto(
                record.GroupId.Value, name, new SessionKey(record.Date, record.Start).ToString(), record.Date,
                AttendanceViews.Format(record.Start), AttendanceViews.Format(record.End), record.Status);
        }

        var list = rows.OrderBy(r => r.Key.Date).ThenBy(r => r.Key.Start).ThenBy(r => r.Value.GroupName, AttendanceViews.Turkish).Select(r => r.Value).ToList();
        return Result.Success(new StudentAttendanceDto(student.Id, student.FirstName, student.LastName, from, to, list, Summarize(list)));

        static StudentSessionDto Row(GroupInfo group, DateOnly date, TimeOnly start, TimeOnly end, AttendanceStatus? status) =>
            new(group.Id, group.Name, new SessionKey(date, start).ToString(), date, AttendanceViews.Format(start), AttendanceViews.Format(end), status);
    }

    private static AttendanceSummaryDto Summarize(List<StudentSessionDto> sessions)
    {
        int Count(AttendanceStatus status) => sessions.Count(s => s.Status == status);
        var (present, late, absent, excused) = (Count(AttendanceStatus.Present), Count(AttendanceStatus.Late), Count(AttendanceStatus.Absent), Count(AttendanceStatus.Excused));
        var counted = present + late + absent;
        decimal? rate = counted == 0 ? null : Math.Round((decimal)(present + late) / counted, 4, MidpointRounding.AwayFromZero);
        return new AttendanceSummaryDto(sessions.Count, present, late, absent, excused, sessions.Count(s => s.Status is null), rate);
    }
}

internal sealed class GetGroupAttendanceMonthHandler(
    ISessionRepository sessions,
    IGroupDirectory groups,
    IEnrollmentHistory enrollments,
    IStudentDirectory students,
    ICurrentUser currentUser) : IQueryHandler<GetGroupAttendanceMonthQuery, GroupAttendanceMonthDto>
{
    public async Task<Result<GroupAttendanceMonthDto>> Handle(GetGroupAttendanceMonthQuery query, CancellationToken cancellationToken)
    {
        var month = YearMonth.Parse(query.Month!).Value;
        var group = await groups.FindVisibleGroupAsync(query.GroupId, currentUser, cancellationToken);
        if (group is null)
        {
            return Result.Failure<GroupAttendanceMonthDto>(GroupErrors.NotFound);
        }

        var stored = await sessions.ListOfGroupAsync(query.GroupId, month.FirstDay, month.LastDay, cancellationToken);
        var columns = group.PlannedBetween(month.FirstDay, month.LastDay)
            .Select(p => (p.Date, p.Time.Start, p.Time.End))
            .Where(p => !stored.Any(s => s.Date == p.Date && s.StartTime == p.Start))
            .Concat(stored.Select(s => (s.Date, Start: s.StartTime, End: s.EndTime)))
            .OrderBy(c => c.Date).ThenBy(c => c.Start)
            .ToList();
        var byKey = stored.ToDictionary(s => (s.Date, s.StartTime));

        var enrolled = (await enrollments.ListOfGroupAsync(query.GroupId, month.FirstDay, month.LastDay, cancellationToken)).Select(p => p.StudentId.Value);
        var recordedStudents = stored.SelectMany(s => s.Records).Select(r => r.StudentId.Value);
        var infos = await students.GetManyAsync([.. enrolled.Union(recordedStudents)], cancellationToken);

        var rows = infos.Values.SortByName().Select(student => new MonthStudentRowDto(
            student.Id,
            student.FirstName,
            student.LastName,
            [.. columns.Select(c => byKey.TryGetValue((c.Date, c.Start), out var session)
                ? session.Records.FirstOrDefault(r => r.StudentId.Value == student.Id)?.Status
                : null)]));

        return Result.Success(new GroupAttendanceMonthDto(
            group.Id,
            month.ToString(),
            [.. columns.Select(c => new MonthSessionDto(new SessionKey(c.Date, c.Start).ToString(), c.Date, AttendanceViews.Format(c.Start), AttendanceViews.Format(c.End), byKey.ContainsKey((c.Date, c.Start))))],
            [.. rows]));
    }
}
