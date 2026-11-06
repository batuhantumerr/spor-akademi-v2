using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Groups;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Endpoints.Groups;

internal sealed record CreateGroupRequest(Guid SportId, string Name, BirthYearRangeDto BirthYearRange, int Capacity, Guid FacilityId, Guid? CoachId);

internal sealed record UpdateGroupRequest(string Name, BirthYearRangeDto BirthYearRange, int Capacity, Guid FacilityId, Guid? CoachId, uint Version);

internal sealed record ReplaceScheduleRequest(IReadOnlyList<SlotInput> Slots, uint Version);

/// <summary>US-22: <c>effectiveFrom</c> is <c>YYYY-MM</c>; the amount is money as text (DEC-066).</summary>
internal sealed record DefineFeeRequest(string EffectiveFrom, MoneyDto Amount);

/// <summary>A non-blocking notice in a successful response (appendix D §1.8); the client builds the text from the code and args.</summary>
internal sealed record WarningDto(string Code, Guid? RelatedId, IReadOnlyDictionary<string, object?> Args)
{
    public static WarningDto From(Warning warning) => new(warning.Code, warning.RelatedId, warning.Arguments);
}

/// <summary>The saved schedule with its new version, plus facility overlap warnings (US-10).</summary>
internal sealed record ScheduleResponse(Guid Id, IReadOnlyList<ScheduleSlotDto> Slots, uint Version, IReadOnlyList<WarningDto> Warnings);

/// <summary>Groups and the coach's own view (appendix D §2.5, §2.6; US-09 – US-12).</summary>
internal static class GroupsEndpoints
{
    public static IEndpointRouteBuilder MapGroupsEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/groups").WithTags("Groups").RequireRole(Role.SchoolAdmin);
        admin.MapGet("/", ListGroups).Produces<PageResponse<GroupDto>>().ProducesProblems(400);
        admin.MapPost("/", CreateGroup).Produces<GroupDto>(StatusCodes.Status201Created).ProducesProblems(400, 404, 409, 422);
        admin.MapPut("/{id:guid}", UpdateGroup).Produces<GroupDto>().ProducesProblems(400, 404, 409, 422);
        admin.MapPut("/{id:guid}/schedule", ReplaceSchedule).Produces<ScheduleResponse>().ProducesProblems(400, 404, 409, 422);
        admin.MapPost("/{id:guid}/archive", ArchiveGroup).Produces<GroupDto>().ProducesProblems(404, 409, 422);
        admin.MapGet("/{id:guid}/fees", ListFees).Produces<IReadOnlyList<GroupFeeDto>>().ProducesProblems(404);
        admin.MapPost("/{id:guid}/fees", DefineFee).Produces<IReadOnlyList<GroupFeeDto>>(StatusCodes.Status201Created).ProducesProblems(400, 404, 409, 422);

        // A coach reaches only own active groups here; any other id is 404 (US-12).
        var shared = app.MapGroup("/api/v1/groups").WithTags("Groups").RequireRole(Role.SchoolAdmin, Role.Coach);
        shared.MapGet("/{id:guid}", GetGroup).Produces<GroupDto>().ProducesProblems(404);
        shared.MapGet("/{id:guid}/sessions", GetGroupSessions).Produces<IReadOnlyList<SessionDto>>().ProducesProblems(400, 404);

        var me = app.MapGroup("/api/v1/me").WithTags("Coach").RequireRole(Role.Coach);
        me.MapGet("/groups", ListMyGroups).Produces<IReadOnlyList<GroupDto>>().ProducesProblems();
        me.MapGet("/sessions", ListMySessions).Produces<IReadOnlyList<SessionDto>>().ProducesProblems(400);

        return app;
    }

    private static async Task<IResult> ListGroups(
        Guid? sportId,
        Guid? coachId,
        GroupStatus? status,
        int? page,
        int? pageSize,
        IQueryHandler<ListGroupsQuery, PagedResult<GroupDto>> handler,
        CancellationToken cancellationToken)
    {
        var query = new ListGroupsQuery(sportId, coachId, status, EndpointConventions.ToPageRequest(page, pageSize));
        var result = await handler.Handle(query, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<GroupDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetGroup(Guid id, IQueryHandler<GetGroupQuery, GroupDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetGroupQuery(GroupId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> CreateGroup(
        CreateGroupRequest request,
        ICommandHandler<CreateGroupCommand, GroupId> handler,
        IQueryHandler<GetGroupQuery, GroupDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new CreateGroupCommand(request.SportId, request.Name, request.BirthYearRange, request.Capacity, request.FacilityId, request.CoachId);
        var created = await handler.Handle(command, cancellationToken);
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var group = await reader.Handle(new GetGroupQuery(created.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/groups/{created.Value.Value}", group.Value);
    }

    private static async Task<IResult> UpdateGroup(
        Guid id,
        UpdateGroupRequest request,
        ICommandHandler<UpdateGroupCommand, Unit> handler,
        IQueryHandler<GetGroupQuery, GroupDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new UpdateGroupCommand(
            GroupId.From(id), request.Name, request.BirthYearRange, request.Capacity, request.FacilityId, request.CoachId, request.Version);
        var updated = await handler.Handle(command, cancellationToken);
        return updated.IsSuccess
            ? (await reader.Handle(new GetGroupQuery(GroupId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(updated.Error);
    }

    private static async Task<IResult> ReplaceSchedule(
        Guid id,
        ReplaceScheduleRequest request,
        ICommandHandler<ReplaceScheduleCommand, IReadOnlyList<Warning>> handler,
        IQueryHandler<GetGroupQuery, GroupDto> reader,
        CancellationToken cancellationToken)
    {
        var replaced = await handler.Handle(new ReplaceScheduleCommand(GroupId.From(id), request.Slots, request.Version), cancellationToken);
        if (replaced.IsFailure)
        {
            return ProblemResults.Problem(replaced.Error);
        }

        var group = await reader.Handle(new GetGroupQuery(GroupId.From(id)), cancellationToken);
        if (group.IsFailure)
        {
            return ProblemResults.Problem(group.Error);
        }

        return TypedResults.Ok(new ScheduleResponse(
            group.Value.Id, group.Value.Slots, group.Value.Version, [.. replaced.Value.Select(WarningDto.From)]));
    }

    private static async Task<IResult> ListFees(Guid id, IQueryHandler<ListGroupFeesQuery, IReadOnlyList<GroupFeeDto>> reader, CancellationToken cancellationToken) =>
        (await reader.Handle(new ListGroupFeesQuery(GroupId.From(id)), cancellationToken)).OkOrProblem();

    /// <summary>US-22: answers with the whole fee history, so the client sees which periods the new fee covers.</summary>
    private static async Task<IResult> DefineFee(
        Guid id,
        DefineFeeRequest request,
        ICommandHandler<DefineGroupFeeCommand, Unit> handler,
        IQueryHandler<ListGroupFeesQuery, IReadOnlyList<GroupFeeDto>> reader,
        CancellationToken cancellationToken)
    {
        var defined = await handler.Handle(new DefineGroupFeeCommand(GroupId.From(id), request.EffectiveFrom, request.Amount), cancellationToken);
        if (defined.IsFailure)
        {
            return ProblemResults.Problem(defined.Error);
        }

        var fees = await reader.Handle(new ListGroupFeesQuery(GroupId.From(id)), cancellationToken);
        return TypedResults.Created($"/api/v1/groups/{id}/fees", fees.Value);
    }

    private static async Task<IResult> ArchiveGroup(
        Guid id,
        ICommandHandler<ArchiveGroupCommand, Unit> handler,
        IQueryHandler<GetGroupQuery, GroupDto> reader,
        CancellationToken cancellationToken)
    {
        var archived = await handler.Handle(new ArchiveGroupCommand(GroupId.From(id)), cancellationToken);
        return archived.IsSuccess
            ? (await reader.Handle(new GetGroupQuery(GroupId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(archived.Error);
    }

    private static async Task<IResult> GetGroupSessions(
        Guid id,
        DateOnly? from,
        DateOnly? to,
        IQueryHandler<GetGroupSessionsQuery, IReadOnlyList<SessionDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetGroupSessionsQuery(GroupId.From(id), from, to), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ListMyGroups(IQueryHandler<ListMyGroupsQuery, IReadOnlyList<GroupDto>> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ListMyGroupsQuery(), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ListMySessions(
        DateOnly? date,
        IQueryHandler<ListMySessionsQuery, IReadOnlyList<SessionDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ListMySessionsQuery(date), cancellationToken)).OkOrProblem();
}
