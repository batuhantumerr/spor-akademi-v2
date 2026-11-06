using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy;

namespace SporAkademi.Api.Endpoints.Tenancy;

internal sealed record CreateSchoolRequest(string Name, string Slug, string TimeZone, NewSchoolAdmin Admin);

/// <summary>The temporary password of the first School Admin is returned once and never again.</summary>
internal sealed record SchoolCreatedResponse(SchoolDto School, Guid AdminId, string TemporaryPassword);

/// <summary>Schools (appendix D §2.2, US-01). Platform Admin only.</summary>
internal static class SchoolEndpoints
{
    public static IEndpointRouteBuilder MapSchoolEndpoints(this IEndpointRouteBuilder app)
    {
        var schools = app.MapGroup("/api/v1/platform/schools").WithTags("Schools").RequireRole(Role.PlatformAdmin);
        schools.MapGet("/", ListSchools).Produces<PageResponse<SchoolListItem>>().ProducesProblems(400);
        schools.MapGet("/{id:guid}", GetSchool).Produces<SchoolDto>().ProducesProblems(404);
        schools.MapPost("/", CreateSchool).Produces<SchoolCreatedResponse>(StatusCodes.Status201Created).ProducesProblems(400, 409);
        return app;
    }

    private static async Task<IResult> ListSchools(
        int? page,
        int? pageSize,
        IQueryHandler<ListSchoolsQuery, PagedResult<SchoolListItem>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListSchoolsQuery(EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<SchoolListItem>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetSchool(Guid id, IQueryHandler<GetSchoolQuery, SchoolDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetSchoolQuery(id), cancellationToken)).OkOrProblem();

    private static async Task<IResult> CreateSchool(
        CreateSchoolRequest request,
        ICommandHandler<CreateSchoolCommand, SchoolCreated> handler,
        IQueryHandler<GetSchoolQuery, SchoolDto> reader,
        CancellationToken cancellationToken)
    {
        var created = await handler.Handle(new CreateSchoolCommand(request.Name, request.Slug, request.TimeZone, request.Admin), cancellationToken);
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var school = await reader.Handle(new GetSchoolQuery(created.Value.SchoolId), cancellationToken);
        return TypedResults.Created(
            $"/api/v1/platform/schools/{created.Value.SchoolId}",
            new SchoolCreatedResponse(school.Value, created.Value.AdminId, created.Value.TemporaryPassword));
    }
}
