using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Announcements;

/// <summary>An announcement as shown to the School Admin and the group's coach (US-25).</summary>
public sealed record AnnouncementDto(Guid Id, Guid GroupId, string GroupName, string Title, string Body, Guid CreatedBy, DateTimeOffset CreatedAt);

/// <summary>Announcements of the current school (tenant filter applies).</summary>
public interface IAnnouncementRepository
{
    void Add(Announcement announcement);

    Task<Announcement?> FindAsync(AnnouncementId id, CancellationToken cancellationToken);

    /// <summary>Announcements of the given groups, newest first.</summary>
    Task<PagedResult<Announcement>> ListOfGroupsAsync(IReadOnlyCollection<GroupId> groupIds, PageRequest page, CancellationToken cancellationToken);
}

public sealed record PublishAnnouncementCommand(GroupId GroupId, string Title, string Body) : ICommand<AnnouncementId>;

public sealed record GetAnnouncementQuery(AnnouncementId Id) : IQuery<AnnouncementDto>;

/// <summary>A group's announcements; a coach only reaches own active groups (others 404).</summary>
public sealed record ListGroupAnnouncementsQuery(GroupId GroupId, PageRequest Page) : IQuery<PagedResult<AnnouncementDto>>;

/// <summary>The calling coach's announcements across own active groups (US-25 "on the home screen").</summary>
public sealed record ListMyAnnouncementsQuery(PageRequest Page) : IQuery<PagedResult<AnnouncementDto>>;

internal sealed class PublishAnnouncementValidator : AbstractValidator<PublishAnnouncementCommand>
{
    public PublishAnnouncementValidator()
    {
        RuleFor(c => c.Title).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Announcement.TitleMaxLength).WithErrorCode("Field.TooLong");
        RuleFor(c => c.Body).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Announcement.BodyMaxLength).WithErrorCode("Field.TooLong");
    }
}

internal sealed class ListGroupAnnouncementsValidator : AbstractValidator<ListGroupAnnouncementsQuery>
{
    public ListGroupAnnouncementsValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
    }
}

internal sealed class ListMyAnnouncementsValidator : AbstractValidator<ListMyAnnouncementsQuery>
{
    public ListMyAnnouncementsValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
    }
}

internal sealed class PublishAnnouncementHandler(
    IAnnouncementRepository announcements,
    IGroupDirectory groups,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : ICommandHandler<PublishAnnouncementCommand, AnnouncementId>
{
    public async Task<Result<AnnouncementId>> Handle(PublishAnnouncementCommand command, CancellationToken cancellationToken)
    {
        var group = await groups.FindAsync(command.GroupId, cancellationToken);
        if (group is null)
        {
            return Result.Failure<AnnouncementId>(GroupErrors.NotFound);
        }

        if (group.IsArchived)
        {
            return Result.Failure<AnnouncementId>(GroupErrors.Archived);
        }

        var author = currentUser.UserId ?? throw new InvalidOperationException("Publishing needs a signed-in user.");
        var published = Announcement.Publish(
            currentUser.RequireTenant(), command.GroupId, UserId.From(author), command.Title, command.Body, timeProvider.GetUtcNow());
        if (published.IsFailure)
        {
            // Backstop of the validator: the body or title was blank or too long after trimming.
            return Result.Failure<AnnouncementId>(ValidationErrors.ForField("body", published.Error));
        }

        announcements.Add(published.Value);
        return Result.Success(published.Value.Id);
    }
}

internal static class AnnouncementViews
{
    public static async Task<PagedResult<AnnouncementDto>> ToDtosAsync(PagedResult<Announcement> page, IGroupDirectory groups, CancellationToken cancellationToken)
    {
        var names = await groups.GetManyAsync([.. page.Items.Select(a => a.GroupId.Value).Distinct()], cancellationToken);
        var items = page.Items
            .Select(a => new AnnouncementDto(
                a.Id.Value,
                a.GroupId.Value,
                names.GetValueOrDefault(a.GroupId.Value)?.Name ?? string.Empty,
                a.Title,
                a.Body,
                a.AuthorId.Value,
                a.PublishedAt))
            .ToList();
        return new PagedResult<AnnouncementDto>(items, page.Page, page.PageSize, page.TotalCount);
    }
}

internal sealed class GetAnnouncementHandler(IAnnouncementRepository announcements, IGroupDirectory groups)
    : IQueryHandler<GetAnnouncementQuery, AnnouncementDto>
{
    public async Task<Result<AnnouncementDto>> Handle(GetAnnouncementQuery query, CancellationToken cancellationToken)
    {
        if (await announcements.FindAsync(query.Id, cancellationToken) is not { } announcement)
        {
            return Result.Failure<AnnouncementDto>(Error.NotFound("Announcement.NotFound", "Announcement not found."));
        }

        var page = await AnnouncementViews.ToDtosAsync(new PagedResult<Announcement>([announcement], 1, 1, 1), groups, cancellationToken);
        return Result.Success(page.Items[0]);
    }
}

internal sealed class ListGroupAnnouncementsHandler(
    IAnnouncementRepository announcements,
    IGroupDirectory groups,
    ICurrentUser currentUser) : IQueryHandler<ListGroupAnnouncementsQuery, PagedResult<AnnouncementDto>>
{
    public async Task<Result<PagedResult<AnnouncementDto>>> Handle(ListGroupAnnouncementsQuery query, CancellationToken cancellationToken)
    {
        var group = await groups.FindAsync(query.GroupId, cancellationToken);
        var visible = group is not null
            && (currentUser.Role != Role.Coach || (!group.IsArchived && currentUser.UserId is { } me && group.CoachId == me));
        if (!visible)
        {
            return Result.Failure<PagedResult<AnnouncementDto>>(GroupErrors.NotFound);
        }

        var page = await announcements.ListOfGroupsAsync([query.GroupId], query.Page, cancellationToken);
        return Result.Success(await AnnouncementViews.ToDtosAsync(page, groups, cancellationToken));
    }
}

internal sealed class ListMyAnnouncementsHandler(
    IAnnouncementRepository announcements,
    IGroupDirectory groups,
    ICoachGroups coachGroups,
    ICurrentUser currentUser) : IQueryHandler<ListMyAnnouncementsQuery, PagedResult<AnnouncementDto>>
{
    public async Task<Result<PagedResult<AnnouncementDto>>> Handle(ListMyAnnouncementsQuery query, CancellationToken cancellationToken)
    {
        var coach = currentUser.UserId is { } id && currentUser.Role == Role.Coach
            ? UserId.From(id)
            : throw new InvalidOperationException("This operation needs a coach.");
        var mine = await coachGroups.ListActiveGroupIdsAsync(coach, cancellationToken);
        var page = await announcements.ListOfGroupsAsync(mine, query.Page, cancellationToken);
        return Result.Success(await AnnouncementViews.ToDtosAsync(page, groups, cancellationToken));
    }
}
