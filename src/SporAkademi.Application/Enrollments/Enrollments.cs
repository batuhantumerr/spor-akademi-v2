using System.Globalization;
using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments;

/// <summary>Enrolls a student (US-17). Without a start date the enrollment starts today in the school's time zone.</summary>
public sealed record EnrollStudentCommand(Guid StudentId, Guid GroupId, DateOnly? StartDate) : ICommand<EnrollmentId>;

/// <summary>Ends an enrollment on <see cref="Date"/> (US-18); the seat is free from that date.</summary>
public sealed record EndEnrollmentCommand(EnrollmentId Id, DateOnly? Date) : ICommand<Unit>;

public sealed record GetEnrollmentQuery(EnrollmentId Id) : IQuery<EnrollmentDto>;

public sealed record ListGroupEnrollmentsQuery(GroupId GroupId, EnrollmentStatus? Status, PageRequest Page) : IQuery<PagedResult<EnrollmentDto>>;

public sealed record ListStudentEnrollmentsQuery(StudentId StudentId) : IQuery<IReadOnlyList<EnrollmentDto>>;

/// <summary>The coach view of a group's active students (US-14). A coach only gets own active groups.</summary>
public sealed record GetRosterQuery(GroupId GroupId) : IQuery<IReadOnlyList<RosterEntryDto>>;

/// <summary>Dates the enrollment endpoints accept (years 2000–2100, like <c>YearMonth</c>).</summary>
internal static class EnrollmentDates
{
    public static bool IsSupported(DateOnly? date) => date is not { } d || d.Year is >= 2000 and <= 2100;

    /// <summary>DEC-160: enrollments start and end today or in the past, so "Active" always means active today.</summary>
    public static Error NotAfterToday(string field) => ValidationErrors.ForField(field, CommonErrors.FieldInvalid);
}

internal sealed class EnrollStudentValidator : AbstractValidator<EnrollStudentCommand>
{
    public EnrollStudentValidator()
    {
        RuleFor(c => c.StudentId).NotEmpty().WithErrorCode("Field.Required");
        RuleFor(c => c.GroupId).NotEmpty().WithErrorCode("Field.Required");
        RuleFor(c => c.StartDate).Must(EnrollmentDates.IsSupported).WithErrorCode("Field.Invalid");
    }
}

internal sealed class EndEnrollmentValidator : AbstractValidator<EndEnrollmentCommand>
{
    public EndEnrollmentValidator() =>
        RuleFor(c => c.Date).NotNull().WithErrorCode("Field.Required")
            .Must(EnrollmentDates.IsSupported).WithErrorCode("Field.Invalid");
}

internal sealed class ListGroupEnrollmentsValidator : AbstractValidator<ListGroupEnrollmentsQuery>
{
    public ListGroupEnrollmentsValidator()
    {
        // Paging errors use the query-string names (page, pageSize).
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
        RuleFor(q => q.Status).IsInEnum().WithErrorCode("Field.Invalid");
    }
}

internal sealed class EnrollStudentHandler(
    IEnrollmentRepository enrollments,
    IEnrollmentLocks locks,
    IStudentDirectory students,
    IGroupDirectory groups,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : ICommandHandler<EnrollStudentCommand, EnrollmentId>
{
    public async Task<Result<EnrollmentId>> Handle(EnrollStudentCommand command, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var today = await tenancy.TodayAsync(tenant, cancellationToken);
        var startDate = command.StartDate ?? today;
        if (startDate > today)
        {
            return Result.Failure<EnrollmentId>(EnrollmentDates.NotAfterToday("startDate"));
        }

        var studentId = StudentId.From(command.StudentId);
        var groupId = GroupId.From(command.GroupId);

        // ADR-0005: everything below is read under the student and group locks, so a concurrent
        // enrollment or capacity change waits until this transaction commits.
        await locks.LockStudentAndGroupAsync(studentId, groupId, cancellationToken);

        var student = await students.FindAsync(studentId, cancellationToken);
        if (student is null)
        {
            return Result.Failure<EnrollmentId>(StudentErrors.NotFound);
        }

        // Only an anonymized student has no birth date, and an anonymized student has left for good (DEC-363).
        if (student.BirthDate is not { } birthDate)
        {
            return Result.Failure<EnrollmentId>(EnrollmentErrors.StudentNotActive);
        }

        var group = await groups.FindAsync(groupId, cancellationToken);
        if (group is null)
        {
            return Result.Failure<EnrollmentId>(GroupErrors.NotFound);
        }

        var current = await enrollments.ListActiveOfStudentAsync(studentId, cancellationToken);
        var currentGroups = await groups.GetManyAsync([.. current.Select(e => e.GroupId.Value).Distinct()], cancellationToken);
        var active = await enrollments.CountActiveAsync(groupId, cancellationToken);

        var enrolled = EnrollmentPolicy.Enroll(
            tenant,
            new EnrollmentStudent(studentId, student.Status, birthDate),
            ToPolicy(group),
            active,
            [.. currentGroups.Values.Select(ToPolicy)],
            startDate);
        if (enrolled.IsFailure)
        {
            return Result.Failure<EnrollmentId>(enrolled.Error);
        }

        enrollments.Add(enrolled.Value);
        return Result.Success(enrolled.Value.Id);
    }

    private static EnrollmentGroup ToPolicy(GroupInfo group) =>
        new(GroupId.From(group.Id), group.Name, group.IsArchived, group.BirthYears, group.Capacity, group.Slots);
}

internal sealed class EndEnrollmentHandler(IEnrollmentRepository enrollments, ITenancyModule tenancy, ICurrentUser currentUser)
    : ICommandHandler<EndEnrollmentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(EndEnrollmentCommand command, CancellationToken cancellationToken)
    {
        var enrollment = await enrollments.GetAsync(command.Id, cancellationToken);
        if (enrollment is null)
        {
            return Result.Failure<Unit>(EnrollmentErrors.NotFound);
        }

        var date = command.Date!.Value;
        if (date > await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken))
        {
            return Result.Failure<Unit>(EnrollmentDates.NotAfterToday("date"));
        }

        var ended = enrollment.End(date);
        return ended.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(ended.Error);
    }
}

/// <summary>Builds enrollment DTOs with student and group names from their modules' contracts.</summary>
internal static class EnrollmentViews
{
    public static async Task<IReadOnlyList<EnrollmentDto>> ToDtosAsync(
        IReadOnlyList<Enrollment> items,
        IStudentDirectory students,
        IGroupDirectory groups,
        CancellationToken cancellationToken)
    {
        var studentInfos = await students.GetManyAsync([.. items.Select(e => e.StudentId.Value).Distinct()], cancellationToken);
        var groupInfos = await groups.GetManyAsync([.. items.Select(e => e.GroupId.Value).Distinct()], cancellationToken);
        return
        [
            .. items.Select(e =>
            {
                var student = studentInfos.GetValueOrDefault(e.StudentId.Value);
                return new EnrollmentDto(
                    e.Id.Value,
                    e.StudentId.Value,
                    student?.FirstName ?? string.Empty,
                    student?.LastName ?? string.Empty,
                    e.GroupId.Value,
                    groupInfos.GetValueOrDefault(e.GroupId.Value)?.Name ?? string.Empty,
                    e.StartDate,
                    e.EndDate,
                    e.Status);
            }),
        ];
    }
}

internal sealed class GetEnrollmentHandler(IEnrollmentRepository enrollments, IStudentDirectory students, IGroupDirectory groups)
    : IQueryHandler<GetEnrollmentQuery, EnrollmentDto>
{
    public async Task<Result<EnrollmentDto>> Handle(GetEnrollmentQuery query, CancellationToken cancellationToken) =>
        await enrollments.FindAsync(query.Id, cancellationToken) is { } enrollment
            ? Result.Success((await EnrollmentViews.ToDtosAsync([enrollment], students, groups, cancellationToken))[0])
            : Result.Failure<EnrollmentDto>(EnrollmentErrors.NotFound);
}

internal sealed class ListGroupEnrollmentsHandler(IEnrollmentRepository enrollments, IStudentDirectory students, IGroupDirectory groups)
    : IQueryHandler<ListGroupEnrollmentsQuery, PagedResult<EnrollmentDto>>
{
    public async Task<Result<PagedResult<EnrollmentDto>>> Handle(ListGroupEnrollmentsQuery query, CancellationToken cancellationToken)
    {
        if (await groups.FindAsync(query.GroupId, cancellationToken) is null)
        {
            return Result.Failure<PagedResult<EnrollmentDto>>(GroupErrors.NotFound);
        }

        var page = await enrollments.ListOfGroupAsync(query.GroupId, query.Status, query.Page, cancellationToken);
        var items = await EnrollmentViews.ToDtosAsync(page.Items, students, groups, cancellationToken);
        return Result.Success(new PagedResult<EnrollmentDto>(items, page.Page, page.PageSize, page.TotalCount));
    }
}

internal sealed class ListStudentEnrollmentsHandler(IEnrollmentRepository enrollments, IStudentDirectory students, IGroupDirectory groups)
    : IQueryHandler<ListStudentEnrollmentsQuery, IReadOnlyList<EnrollmentDto>>
{
    public async Task<Result<IReadOnlyList<EnrollmentDto>>> Handle(ListStudentEnrollmentsQuery query, CancellationToken cancellationToken)
    {
        if (await students.FindAsync(query.StudentId, cancellationToken) is null)
        {
            return Result.Failure<IReadOnlyList<EnrollmentDto>>(StudentErrors.NotFound);
        }

        var history = await enrollments.ListOfStudentAsync(query.StudentId, cancellationToken);
        return Result.Success(await EnrollmentViews.ToDtosAsync(history, students, groups, cancellationToken));
    }
}

internal sealed class GetRosterHandler(
    IEnrollmentRepository enrollments,
    IStudentDirectory students,
    IGroupDirectory groups,
    ICurrentUser currentUser) : IQueryHandler<GetRosterQuery, IReadOnlyList<RosterEntryDto>>
{
    private static readonly StringComparer Turkish = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    public async Task<Result<IReadOnlyList<RosterEntryDto>>> Handle(GetRosterQuery query, CancellationToken cancellationToken)
    {
        var group = await groups.FindAsync(query.GroupId, cancellationToken);
        if (group is null || !IsVisible(group))
        {
            return Result.Failure<IReadOnlyList<RosterEntryDto>>(GroupErrors.NotFound);
        }

        var active = await enrollments.ActiveStudentsOfGroupsAsync([query.GroupId], cancellationToken);
        var infos = await students.GetManyAsync([.. active.Select(s => s.Value)], cancellationToken);
        return Result.Success<IReadOnlyList<RosterEntryDto>>(
        [
            .. infos.Values
                // Only active students are on a roster; an anonymized student (no birth date, no contact) never is.
                .Where(s => s.BirthDate is not null && s.EmergencyContactPhone is not null)
                .OrderBy(s => s.LastName, Turkish).ThenBy(s => s.FirstName, Turkish)
                .Select(s => new RosterEntryDto(s.FirstName, s.LastName, s.BirthDate!.Value.Year, s.EmergencyContactPhone!)),
        ]);
    }

    /// <summary>A coach sees only own active groups (US-12, US-14); others are "not found".</summary>
    private bool IsVisible(GroupInfo group) =>
        currentUser.Role != Role.Coach || (!group.IsArchived && currentUser.UserId is { } me && group.CoachId == me);
}

/// <summary>The Enrollments module's contracts for other modules (counts, ending, student filters).</summary>
internal sealed class EnrollmentModule(IEnrollmentRepository enrollments, IGroupDirectory groups)
    : IEnrollmentCounts, IEnrollmentEnder, IStudentGroupFilter, IEnrollmentHistory
{
    public async Task<IReadOnlyList<EnrollmentPeriod>> ListOfGroupAsync(GroupId groupId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var ofGroup = await enrollments.ListOfGroupBetweenAsync(groupId, firstDay, lastDay, cancellationToken);
        var ended = ofGroup.Where(e => e.EndDate is not null).Select(e => e.StudentId).Distinct().ToList();
        var ofTheirStudents = ended.Count == 0 ? [] : await enrollments.ListOfStudentsAsync(ended, cancellationToken);
        return [.. ofGroup.Select(e => ToPeriod(e, ofTheirStudents))];
    }

    public async Task<IReadOnlyList<EnrollmentPeriod>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        var all = await enrollments.ListOfStudentAsync(studentId, cancellationToken);
        return [.. all.Select(e => ToPeriod(e, all))];
    }

    /// <summary>DEC-170: an enrollment hands its end day over when the same student starts another one that day.</summary>
    private static EnrollmentPeriod ToPeriod(Enrollment enrollment, IReadOnlyCollection<Enrollment> ofSameStudents) =>
        new(
            enrollment.StudentId,
            enrollment.GroupId,
            enrollment.StartDate,
            enrollment.EndDate,
            enrollment.EndDate is { } end
                && ofSameStudents.Any(other => other.Id != enrollment.Id && other.StudentId == enrollment.StudentId && other.StartDate == end));

    public Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken) =>
        enrollments.CountActiveAsync(groupId, cancellationToken);

    public async Task<Result> EndActiveEnrollmentsAsync(GroupId groupId, DateOnly endDate, CancellationToken cancellationToken)
    {
        foreach (var enrollment in await enrollments.ListActiveOfGroupAsync(groupId, cancellationToken))
        {
            var ended = enrollment.End(endDate);
            if (ended.IsFailure)
            {
                return ended;
            }
        }

        return Result.Success();
    }

    public async Task<Result> EndActiveEnrollmentsOfStudentAsync(StudentId studentId, DateOnly endDate, CancellationToken cancellationToken)
    {
        var active = await enrollments.ListActiveOfStudentAsync(studentId, cancellationToken);
        if (active.Any(e => e.StartDate > endDate))
        {
            // US-15: a student cannot leave before an enrollment even started.
            return Result.Failure(StudentErrors.InvalidLeaveDate);
        }

        foreach (var enrollment in active)
        {
            var ended = enrollment.End(endDate);
            if (ended.IsFailure)
            {
                return ended;
            }
        }

        return Result.Success();
    }

    public async Task<IReadOnlyCollection<StudentId>?> StudentsEnrolledInAsync(GroupId? groupId, SportId? sportId, CancellationToken cancellationToken)
    {
        if (groupId is null && sportId is null)
        {
            return null;
        }

        IReadOnlyCollection<GroupId> groupIds = groupId is { } group ? [group] : [];
        if (sportId is { } sport)
        {
            var ofSport = await groups.ListIdsOfSportAsync(sport, cancellationToken);
            groupIds = groupId is null ? ofSport : [.. groupIds.Intersect(ofSport)];
        }

        return groupIds.Count == 0 ? [] : await enrollments.ActiveStudentsOfGroupsAsync(groupIds, cancellationToken);
    }
}
