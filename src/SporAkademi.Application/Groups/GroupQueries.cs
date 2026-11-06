using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Groups;

/// <summary>Group detail. A coach only sees own active groups; anything else is "not found" (US-12).</summary>
public sealed record GetGroupQuery(GroupId Id) : IQuery<GroupDto>;

public sealed record ListGroupsQuery(Guid? SportId, Guid? CoachId, GroupStatus? Status, PageRequest Page) : IQuery<PagedResult<GroupDto>>;

/// <summary>Planned sessions of a group, both dates inclusive, at most <see cref="MaxDays"/> days.</summary>
public sealed record GetGroupSessionsQuery(GroupId Id, DateOnly? From, DateOnly? To) : IQuery<IReadOnlyList<SessionDto>>
{
    public const int MaxDays = 62;
}

/// <summary>Calendar dates the session endpoints accept (like <c>YearMonth</c>: years 2000–2100).</summary>
internal static class SessionDates
{
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    public static bool IsSupported(DateOnly? date) => date is not { } d || d.Year is >= MinYear and <= MaxYear;
}

/// <summary>The calling coach's active groups (US-12).</summary>
public sealed record ListMyGroupsQuery : IQuery<IReadOnlyList<GroupDto>>;

/// <summary>The calling coach's sessions on <see cref="Date"/>, by default today in the school's time zone (US-12).</summary>
public sealed record ListMySessionsQuery(DateOnly? Date) : IQuery<IReadOnlyList<SessionDto>>;

internal sealed class ListGroupsValidator : AbstractValidator<ListGroupsQuery>
{
    public ListGroupsValidator()
    {
        // Paging errors use the query-string names (page, pageSize).
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
        RuleFor(q => q.Status).IsInEnum().WithErrorCode("Field.Invalid");
    }
}

internal sealed class GetGroupSessionsValidator : AbstractValidator<GetGroupSessionsQuery>
{
    public GetGroupSessionsValidator()
    {
        RuleFor(q => q.From).NotNull().WithErrorCode("Field.Required")
            .Must(SessionDates.IsSupported).WithErrorCode("Field.Invalid");
        RuleFor(q => q.To).NotNull().WithErrorCode("Field.Required")
            .Must(SessionDates.IsSupported).WithErrorCode("Field.Invalid");
        RuleFor(q => q.To)
            .Must((q, to) => to!.Value >= q.From!.Value && to.Value.DayNumber - q.From.Value.DayNumber < GetGroupSessionsQuery.MaxDays)
            .WithErrorCode("Field.Invalid")
            .When(q => q.From is not null && q.To is not null);
    }
}

internal sealed class ListMySessionsValidator : AbstractValidator<ListMySessionsQuery>
{
    public ListMySessionsValidator() =>
        RuleFor(q => q.Date).Must(SessionDates.IsSupported).WithErrorCode("Field.Invalid");
}

/// <summary>Turns groups into DTOs, fetching names from the Organization and Identity contracts in batches.</summary>
internal static class GroupViews
{
    public static async Task<IReadOnlyList<GroupDto>> ToDtosAsync(
        IReadOnlyList<GroupSnapshot> snapshots,
        IOrganizationDirectory organization,
        ICoachDirectory coaches,
        CancellationToken cancellationToken)
    {
        var sports = await organization.GetSportsAsync([.. snapshots.Select(s => s.Group.SportId.Value).Distinct()], cancellationToken);
        var facilities = await organization.GetFacilitiesAsync([.. snapshots.Select(s => s.Group.FacilityId.Value).Distinct()], cancellationToken);
        var coachIds = snapshots.Select(s => s.Group.CoachId).OfType<UserId>().Select(c => c.Value).Distinct().ToList();
        var coachInfos = coachIds.Count == 0
            ? new Dictionary<Guid, CoachInfo>()
            : await coaches.GetManyAsync(coachIds, cancellationToken);

        return [.. snapshots.Select(s => ToDto(s, sports, facilities, coachInfos))];
    }

    public static async Task<GroupDto> ToDtoAsync(
        GroupSnapshot snapshot,
        IOrganizationDirectory organization,
        ICoachDirectory coaches,
        CancellationToken cancellationToken) =>
        (await ToDtosAsync([snapshot], organization, coaches, cancellationToken))[0];

    /// <summary>A coach sees own active groups only (US-12); the School Admin sees all of the school.</summary>
    public static bool IsVisibleTo(this Group group, ICurrentUser currentUser) =>
        currentUser.Role != Role.Coach
        || (!group.IsArchived && currentUser.UserId is { } me && group.CoachId == UserId.From(me));

    public static UserId RequireCoach(this ICurrentUser currentUser) =>
        currentUser.UserId is { } id && currentUser.Role == Role.Coach
            ? UserId.From(id)
            : throw new InvalidOperationException("This operation needs a coach.");

    public static async Task<IReadOnlyList<SessionDto>> ToSessionDtosAsync(
        IEnumerable<(Group Group, PlannedSession Session)> sessions,
        IOrganizationDirectory organization,
        CancellationToken cancellationToken)
    {
        var list = sessions.ToList();
        var facilities = await organization.GetFacilitiesAsync([.. list.Select(s => s.Group.FacilityId.Value).Distinct()], cancellationToken);
        return
        [
            .. list
                .OrderBy(s => s.Session.Date).ThenBy(s => s.Session.Time.Start).ThenBy(s => s.Group.NameKey, StringComparer.Ordinal)
                .Select(s => new SessionDto(
                    s.Group.Id.Value,
                    s.Group.Name,
                    s.Session.Date,
                    TimeFormat.Format(s.Session.Time.Start),
                    TimeFormat.Format(s.Session.Time.End),
                    Reference(s.Group.FacilityId.Value, facilities))),
        ];
    }

    private static GroupDto ToDto(
        GroupSnapshot snapshot,
        IReadOnlyDictionary<Guid, CatalogItem> sports,
        IReadOnlyDictionary<Guid, CatalogItem> facilities,
        IReadOnlyDictionary<Guid, CoachInfo> coaches)
    {
        var group = snapshot.Group;
        var coach = group.CoachId is { } coachId
            ? coaches.TryGetValue(coachId.Value, out var info)
                ? new GroupCoachDto(info.Id, info.FirstName, info.LastName, info.IsActive)
                : new GroupCoachDto(coachId.Value, string.Empty, string.Empty, false)
            : null;
        return new GroupDto(
            group.Id.Value,
            group.Name,
            Reference(group.SportId.Value, sports),
            Reference(group.FacilityId.Value, facilities),
            coach,
            new BirthYearRangeDto(group.BirthYearRange.From, group.BirthYearRange.To),
            group.Capacity,
            group.Status,
            group.ArchivedOn,
            [.. group.ScheduleSlots.Select(s => s.ToDto())],
            snapshot.Version);
    }

    private static NamedReferenceDto Reference(Guid id, IReadOnlyDictionary<Guid, CatalogItem> items) =>
        new(id, items.TryGetValue(id, out var item) ? item.Name : string.Empty);
}

internal sealed class GetGroupHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ICoachDirectory coaches,
    ICurrentUser currentUser) : IQueryHandler<GetGroupQuery, GroupDto>
{
    public async Task<Result<GroupDto>> Handle(GetGroupQuery query, CancellationToken cancellationToken)
    {
        var snapshot = await groups.FindAsync(query.Id, cancellationToken);
        return snapshot is not null && snapshot.Group.IsVisibleTo(currentUser)
            ? Result.Success(await GroupViews.ToDtoAsync(snapshot, organization, coaches, cancellationToken))
            : Result.Failure<GroupDto>(GroupErrors.NotFound);
    }
}

internal sealed class ListGroupsHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ICoachDirectory coaches) : IQueryHandler<ListGroupsQuery, PagedResult<GroupDto>>
{
    public async Task<Result<PagedResult<GroupDto>>> Handle(ListGroupsQuery query, CancellationToken cancellationToken)
    {
        var filter = new GroupFilter(
            query.SportId is { } sport ? SportId.From(sport) : null,
            query.CoachId is { } coach ? UserId.From(coach) : null,
            query.Status);
        var page = await groups.ListAsync(filter, query.Page, cancellationToken);
        var items = await GroupViews.ToDtosAsync(page.Items, organization, coaches, cancellationToken);
        return Result.Success(new PagedResult<GroupDto>(items, page.Page, page.PageSize, page.TotalCount));
    }
}

internal sealed class GetGroupSessionsHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ICurrentUser currentUser) : IQueryHandler<GetGroupSessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(GetGroupSessionsQuery query, CancellationToken cancellationToken)
    {
        var snapshot = await groups.FindAsync(query.Id, cancellationToken);
        if (snapshot is null || !snapshot.Group.IsVisibleTo(currentUser))
        {
            return Result.Failure<IReadOnlyList<SessionDto>>(GroupErrors.NotFound);
        }

        var group = snapshot.Group;
        var sessions = SessionCalendar.Between(group, query.From!.Value, query.To!.Value).Select(s => (group, s));
        return Result.Success(await GroupViews.ToSessionDtosAsync(sessions, organization, cancellationToken));
    }
}

internal sealed class ListMyGroupsHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ICoachDirectory coaches,
    ICurrentUser currentUser) : IQueryHandler<ListMyGroupsQuery, IReadOnlyList<GroupDto>>
{
    public async Task<Result<IReadOnlyList<GroupDto>>> Handle(ListMyGroupsQuery query, CancellationToken cancellationToken)
    {
        var mine = await groups.ListActiveOfCoachAsync(currentUser.RequireCoach(), cancellationToken);
        return Result.Success(await GroupViews.ToDtosAsync(mine, organization, coaches, cancellationToken));
    }
}

internal sealed class ListMySessionsHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<ListMySessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(ListMySessionsQuery query, CancellationToken cancellationToken)
    {
        var date = query.Date ?? await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
        var mine = await groups.ListActiveOfCoachAsync(currentUser.RequireCoach(), cancellationToken);
        var sessions = mine.SelectMany(s => SessionCalendar.Between(s.Group, date, date).Select(session => (s.Group, session)));
        return Result.Success(await GroupViews.ToSessionDtosAsync(sessions, organization, cancellationToken));
    }
}
