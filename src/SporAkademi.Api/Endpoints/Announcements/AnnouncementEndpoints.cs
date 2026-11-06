using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Announcements;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Groups;

namespace SporAkademi.Api.Endpoints.Announcements;

internal sealed record PublishAnnouncementRequest(string Title, string Body);

/// <summary>Group announcements (appendix D §2.6, §2.11; US-25).</summary>
internal static class AnnouncementEndpoints
{
    public static IEndpointRouteBuilder MapAnnouncementEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/groups").WithTags("Announcements").RequireRole(Role.SchoolAdmin);
        admin.MapPost("/{id:guid}/announcements", Publish).Produces<AnnouncementDto>(StatusCodes.Status201Created).ProducesProblems(400, 404, 422);

        // The group's coach reads too; any other group (or an archived one) is 404 for a coach.
        var shared = app.MapGroup("/api/v1/groups").WithTags("Announcements").RequireRole(Role.SchoolAdmin, Role.Coach);
        shared.MapGet("/{id:guid}/announcements", ListOfGroup).Produces<PageResponse<AnnouncementDto>>().ProducesProblems(400, 404);

        var me = app.MapGroup("/api/v1/me").WithTags("Coach").RequireRole(Role.Coach);
        me.MapGet("/announcements", ListMine).Produces<PageResponse<AnnouncementDto>>().ProducesProblems(400);

        return app;
    }

    private static async Task<IResult> Publish(
        Guid id,
        PublishAnnouncementRequest request,
        ICommandHandler<PublishAnnouncementCommand, AnnouncementId> handler,
        IQueryHandler<GetAnnouncementQuery, AnnouncementDto> reader,
        CancellationToken cancellationToken)
    {
        var published = await handler.Handle(new PublishAnnouncementCommand(GroupId.From(id), request.Title, request.Body), cancellationToken);
        if (published.IsFailure)
        {
            return ProblemResults.Problem(published.Error);
        }

        var announcement = await reader.Handle(new GetAnnouncementQuery(published.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/groups/{id}/announcements", announcement.Value);
    }

    private static async Task<IResult> ListOfGroup(
        Guid id,
        int? page,
        int? pageSize,
        IQueryHandler<ListGroupAnnouncementsQuery, PagedResult<AnnouncementDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListGroupAnnouncementsQuery(GroupId.From(id), EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<AnnouncementDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> ListMine(
        int? page,
        int? pageSize,
        IQueryHandler<ListMyAnnouncementsQuery, PagedResult<AnnouncementDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListMyAnnouncementsQuery(EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<AnnouncementDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }
}
