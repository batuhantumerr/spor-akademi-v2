using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Announcements;

/// <summary>An announcement as the parent sees it (P3-03, DEC-384): without the author's user id.</summary>
public sealed record ParentAnnouncementDto(Guid Id, Guid GroupId, string GroupName, string Title, string Body, DateTimeOffset PublishedAt);

/// <summary>
/// P3-03: the announcements of the groups the caller's child is enrolled in today (active groups only), newest first.
/// The whole history of those groups is shown, as the group's coach sees it (DEC-384).
/// </summary>
public sealed record ListChildAnnouncementsQuery(StudentId StudentId, PageRequest Page) : IQuery<PagedResult<ParentAnnouncementDto>>;

internal sealed class ListChildAnnouncementsValidator : AbstractValidator<ListChildAnnouncementsQuery>
{
    public ListChildAnnouncementsValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
    }
}

internal sealed class ListChildAnnouncementsHandler(
    IParentChildren children,
    IEnrollmentHistory enrollments,
    IGroupDirectory groups,
    IAnnouncementRepository announcements,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<ListChildAnnouncementsQuery, PagedResult<ParentAnnouncementDto>>
{
    public async Task<Result<PagedResult<ParentAnnouncementDto>>> Handle(ListChildAnnouncementsQuery query, CancellationToken cancellationToken)
    {
        if (!await children.IsMineAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<PagedResult<ParentAnnouncementDto>>(StudentErrors.NotFound);
        }

        var today = await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
        var current = (await enrollments.ListOfStudentAsync(query.StudentId, cancellationToken))
            .Where(p => p.Covers(today))
            .Select(p => p.GroupId)
            .Distinct()
            .ToList();
        var active = await groups.GetManyAsync([.. current.Select(g => g.Value)], cancellationToken);
        var visible = current.Where(g => active.TryGetValue(g.Value, out var info) && !info.IsArchived).ToList();
        if (visible.Count == 0)
        {
            return Result.Success(new PagedResult<ParentAnnouncementDto>([], query.Page.Page, query.Page.PageSize, 0));
        }

        var page = await announcements.ListOfGroupsAsync(visible, query.Page, cancellationToken);
        var items = page.Items
            .Select(a => new ParentAnnouncementDto(a.Id.Value, a.GroupId.Value, active[a.GroupId.Value].Name, a.Title, a.Body, a.PublishedAt))
            .ToList();
        return Result.Success(new PagedResult<ParentAnnouncementDto>(items, page.Page, page.PageSize, page.TotalCount));
    }
}
