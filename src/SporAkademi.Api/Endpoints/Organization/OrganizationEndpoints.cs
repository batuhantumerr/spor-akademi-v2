using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Organization;
using SporAkademi.Domain.Organization;

namespace SporAkademi.Api.Endpoints.Organization;

internal sealed record SportRequest(string Name);

internal sealed record RenameSportRequest(string Name, uint Version);

internal sealed record FacilityRequest(string Name, FacilityType Type, AddressDto Address, int? Capacity);

internal sealed record UpdateFacilityRequest(string Name, FacilityType Type, AddressDto Address, int? Capacity, uint Version);

/// <summary>Sports and facilities (appendix D §2.4, US-06, US-07). School Admin only.</summary>
internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var sports = app.MapGroup("/api/v1/sports").WithTags("Sports").RequireRole(Role.SchoolAdmin);
        sports.MapGet("/", ListSports).Produces<PageResponse<SportDto>>().ProducesProblems(400);
        sports.MapGet("/{id:guid}", GetSport).Produces<SportDto>().ProducesProblems(404);
        sports.MapPost("/", CreateSport).Produces<SportDto>(StatusCodes.Status201Created).ProducesProblems(400, 409);
        sports.MapPut("/{id:guid}", RenameSport).Produces<SportDto>().ProducesProblems(400, 404, 409);
        sports.MapPost("/{id:guid}/archive", ArchiveSport).Produces<SportDto>().ProducesProblems(404);
        sports.MapDelete("/{id:guid}", DeleteSport).Produces(StatusCodes.Status204NoContent).ProducesProblems(404, 409);

        var facilities = app.MapGroup("/api/v1/facilities").WithTags("Facilities").RequireRole(Role.SchoolAdmin);
        facilities.MapGet("/", ListFacilities).Produces<PageResponse<FacilityDto>>().ProducesProblems(400);
        facilities.MapGet("/{id:guid}", GetFacility).Produces<FacilityDto>().ProducesProblems(404);
        facilities.MapPost("/", CreateFacility).Produces<FacilityDto>(StatusCodes.Status201Created).ProducesProblems(400, 409);
        facilities.MapPut("/{id:guid}", UpdateFacility).Produces<FacilityDto>().ProducesProblems(400, 404, 409);
        facilities.MapPost("/{id:guid}/archive", ArchiveFacility).Produces<FacilityDto>().ProducesProblems(404);
        facilities.MapDelete("/{id:guid}", DeleteFacility).Produces(StatusCodes.Status204NoContent).ProducesProblems(404, 409);

        return app;
    }

    private static async Task<IResult> ListSports(
        bool? includeArchived,
        int? page,
        int? pageSize,
        IQueryHandler<ListSportsQuery, PagedResult<SportDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListSportsQuery(includeArchived ?? false, EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<SportDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetSport(Guid id, IQueryHandler<GetSportQuery, SportDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetSportQuery(SportId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> CreateSport(
        SportRequest request,
        ICommandHandler<CreateSportCommand, SportId> handler,
        IQueryHandler<GetSportQuery, SportDto> reader,
        CancellationToken cancellationToken)
    {
        var created = await handler.Handle(new CreateSportCommand(request.Name), cancellationToken);
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var sport = await reader.Handle(new GetSportQuery(created.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/sports/{created.Value.Value}", sport.Value);
    }

    private static async Task<IResult> RenameSport(
        Guid id,
        RenameSportRequest request,
        ICommandHandler<RenameSportCommand, Unit> handler,
        IQueryHandler<GetSportQuery, SportDto> reader,
        CancellationToken cancellationToken)
    {
        var renamed = await handler.Handle(new RenameSportCommand(SportId.From(id), request.Name, request.Version), cancellationToken);
        return renamed.IsSuccess
            ? (await reader.Handle(new GetSportQuery(SportId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(renamed.Error);
    }

    private static async Task<IResult> ArchiveSport(
        Guid id,
        ICommandHandler<ArchiveSportCommand, Unit> handler,
        IQueryHandler<GetSportQuery, SportDto> reader,
        CancellationToken cancellationToken)
    {
        var archived = await handler.Handle(new ArchiveSportCommand(SportId.From(id)), cancellationToken);
        return archived.IsSuccess
            ? (await reader.Handle(new GetSportQuery(SportId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(archived.Error);
    }

    private static async Task<IResult> DeleteSport(Guid id, ICommandHandler<DeleteSportCommand, Unit> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new DeleteSportCommand(SportId.From(id)), cancellationToken)).NoContentOrProblem();

    private static async Task<IResult> ListFacilities(
        bool? includeArchived,
        int? page,
        int? pageSize,
        IQueryHandler<ListFacilitiesQuery, PagedResult<FacilityDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListFacilitiesQuery(includeArchived ?? false, EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<FacilityDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetFacility(Guid id, IQueryHandler<GetFacilityQuery, FacilityDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetFacilityQuery(FacilityId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> CreateFacility(
        FacilityRequest request,
        ICommandHandler<CreateFacilityCommand, FacilityId> handler,
        IQueryHandler<GetFacilityQuery, FacilityDto> reader,
        CancellationToken cancellationToken)
    {
        var created = await handler.Handle(new CreateFacilityCommand(request.Name, request.Type, request.Address, request.Capacity), cancellationToken);
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var facility = await reader.Handle(new GetFacilityQuery(created.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/facilities/{created.Value.Value}", facility.Value);
    }

    private static async Task<IResult> UpdateFacility(
        Guid id,
        UpdateFacilityRequest request,
        ICommandHandler<UpdateFacilityCommand, Unit> handler,
        IQueryHandler<GetFacilityQuery, FacilityDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new UpdateFacilityCommand(FacilityId.From(id), request.Name, request.Type, request.Address, request.Capacity, request.Version);
        var updated = await handler.Handle(command, cancellationToken);
        return updated.IsSuccess
            ? (await reader.Handle(new GetFacilityQuery(FacilityId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(updated.Error);
    }

    private static async Task<IResult> ArchiveFacility(
        Guid id,
        ICommandHandler<ArchiveFacilityCommand, Unit> handler,
        IQueryHandler<GetFacilityQuery, FacilityDto> reader,
        CancellationToken cancellationToken)
    {
        var archived = await handler.Handle(new ArchiveFacilityCommand(FacilityId.From(id)), cancellationToken);
        return archived.IsSuccess
            ? (await reader.Handle(new GetFacilityQuery(FacilityId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(archived.Error);
    }

    private static async Task<IResult> DeleteFacility(Guid id, ICommandHandler<DeleteFacilityCommand, Unit> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new DeleteFacilityCommand(FacilityId.From(id)), cancellationToken)).NoContentOrProblem();
}
