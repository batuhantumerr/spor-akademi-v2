using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Students;
using SporAkademi.Application.Tenancy;

namespace SporAkademi.Api.Endpoints.Tenancy;

internal sealed record UpdateRetentionPolicyRequest(int PersonalDataMonths, uint Version);

/// <summary>
/// P2-F5: the School Admin's data retention setting (DEC-361) and the latest anonymization runs (DEC-364). Both act
/// on the caller's own school; there is no school id in the route or the body.
/// </summary>
internal static class DataRetentionEndpoints
{
    public static IEndpointRouteBuilder MapDataRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        var retention = app.MapGroup("/api/v1/settings/data-retention").WithTags("Settings").RequireRole(Role.SchoolAdmin);
        retention.MapGet("/", GetPolicy).Produces<RetentionPolicyDto>().ProducesProblems(404);
        retention.MapPut("/", UpdatePolicy).Produces<RetentionPolicyDto>().ProducesProblems(400, 404, 409);
        retention.MapGet("/runs", ListRuns).Produces<IReadOnlyList<AnonymizationRunDto>>();
        retention.MapGet("/impact", GetImpact).Produces<RetentionImpactDto>().ProducesProblems(400);
        return app;
    }

    private static async Task<IResult> GetImpact(
        int? personalDataMonths,
        IQueryHandler<GetRetentionImpactQuery, RetentionImpactDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRetentionImpactQuery(personalDataMonths), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetPolicy(IQueryHandler<GetRetentionPolicyQuery, RetentionPolicyDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRetentionPolicyQuery(), cancellationToken)).OkOrProblem();

    private static async Task<IResult> UpdatePolicy(
        UpdateRetentionPolicyRequest request,
        ICommandHandler<UpdateRetentionPolicyCommand, Unit> handler,
        IQueryHandler<GetRetentionPolicyQuery, RetentionPolicyDto> reader,
        CancellationToken cancellationToken)
    {
        var updated = await handler.Handle(new UpdateRetentionPolicyCommand(request.PersonalDataMonths, request.Version), cancellationToken);
        return updated.IsSuccess
            ? (await reader.Handle(new GetRetentionPolicyQuery(), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(updated.Error);
    }

    private static async Task<IResult> ListRuns(
        IQueryHandler<ListAnonymizationRunsQuery, IReadOnlyList<AnonymizationRunDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ListAnonymizationRunsQuery(), cancellationToken)).OkOrProblem();
}
