using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Audit;

namespace SporAkademi.Api.Endpoints.Audit;

/// <summary>
/// P4-05: the school's personal data audit log, School Admin only (coaches and parents get 403). Reading the log is not
/// itself recorded (DEC-532): it shows ids and field names, not the audited data.
/// </summary>
internal static class AuditLogEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/v1/audit-log").WithTags("Audit").RequireRole(Role.SchoolAdmin);
        audit.MapGet("/", List).Produces<PageResponse<AuditEntryDto>>().ProducesProblems(400);
        audit.MapGet("/actors", Actors).Produces<IReadOnlyList<AuditActorDto>>();
        return app;
    }

    /// <param name="entityType">Student, Guardian, StudentDocument or User.</param>
    /// <param name="from">First day (inclusive), in the school's time zone.</param>
    /// <param name="to">Last day (inclusive), in the school's time zone.</param>
    private static async Task<IResult> List(
        string? entityType,
        Guid? userId,
        DateOnly? from,
        DateOnly? to,
        int? page,
        int? pageSize,
        IQueryHandler<ListAuditLogQuery, PagedResult<AuditEntryDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ListAuditLogQuery(entityType, userId, from, to, EndpointConventions.ToPageRequest(page, pageSize)),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<AuditEntryDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> Actors(IQueryHandler<ListAuditActorsQuery, IReadOnlyList<AuditActorDto>> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ListAuditActorsQuery(), cancellationToken)).OkOrProblem();
}
