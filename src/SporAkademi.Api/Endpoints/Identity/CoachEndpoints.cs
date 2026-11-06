using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Coaches;

namespace SporAkademi.Api.Endpoints.Identity;

internal sealed record CreateCoachRequest(string FirstName, string LastName, string Email, string? Phone);

internal sealed record UpdateCoachRequest(string FirstName, string LastName, string? Phone, uint Version);

/// <summary>The coach's temporary password is returned once and never again.</summary>
internal sealed record CoachCreatedResponse(CoachDto Coach, string TemporaryPassword);

/// <summary>Coach accounts (appendix D §2.3, US-08). School Admin only, own school only.</summary>
internal static class CoachEndpoints
{
    public static IEndpointRouteBuilder MapCoachEndpoints(this IEndpointRouteBuilder app)
    {
        var coaches = app.MapGroup("/api/v1/coaches").WithTags("Coaches").RequireRole(Role.SchoolAdmin);
        coaches.MapGet("/", ListCoaches).Produces<PageResponse<CoachDto>>().ProducesProblems(400);
        coaches.MapGet("/{id:guid}", GetCoach).Produces<CoachDto>().ProducesProblems(404);
        coaches.MapPost("/", CreateCoach).Produces<CoachCreatedResponse>(StatusCodes.Status201Created).ProducesProblems(400, 409);
        coaches.MapPut("/{id:guid}", UpdateCoach).Produces<CoachDto>().ProducesProblems(400, 404, 409);
        coaches.MapPost("/{id:guid}/deactivate", DeactivateCoach).Produces<CoachDto>().ProducesProblems(404, 422);
        return app;
    }

    private static async Task<IResult> ListCoaches(
        int? page,
        int? pageSize,
        IQueryHandler<ListCoachesQuery, PagedResult<CoachDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListCoachesQuery(EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<CoachDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetCoach(Guid id, IQueryHandler<GetCoachQuery, CoachDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetCoachQuery(id), cancellationToken)).OkOrProblem();

    private static async Task<IResult> CreateCoach(
        CreateCoachRequest request,
        ICommandHandler<CreateCoachCommand, CoachCreated> handler,
        IQueryHandler<GetCoachQuery, CoachDto> reader,
        CancellationToken cancellationToken)
    {
        var created = await handler.Handle(new CreateCoachCommand(request.FirstName, request.LastName, request.Email, request.Phone), cancellationToken);
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var coach = await reader.Handle(new GetCoachQuery(created.Value.CoachId), cancellationToken);
        return TypedResults.Created(
            $"/api/v1/coaches/{created.Value.CoachId}",
            new CoachCreatedResponse(coach.Value, created.Value.TemporaryPassword));
    }

    private static async Task<IResult> UpdateCoach(
        Guid id,
        UpdateCoachRequest request,
        ICommandHandler<UpdateCoachCommand, Unit> handler,
        IQueryHandler<GetCoachQuery, CoachDto> reader,
        CancellationToken cancellationToken)
    {
        var updated = await handler.Handle(new UpdateCoachCommand(id, request.FirstName, request.LastName, request.Phone, request.Version), cancellationToken);
        return updated.IsSuccess
            ? (await reader.Handle(new GetCoachQuery(id), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(updated.Error);
    }

    private static async Task<IResult> DeactivateCoach(
        Guid id,
        ICommandHandler<DeactivateCoachCommand, Unit> handler,
        IQueryHandler<GetCoachQuery, CoachDto> reader,
        CancellationToken cancellationToken)
    {
        var deactivated = await handler.Handle(new DeactivateCoachCommand(id), cancellationToken);
        return deactivated.IsSuccess
            ? (await reader.Handle(new GetCoachQuery(id), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(deactivated.Error);
    }
}
