using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;

namespace SporAkademi.Api.Endpoints.Enrollments;

internal sealed record EnrollRequest(Guid StudentId, Guid GroupId, DateOnly? StartDate);

internal sealed record EndEnrollmentRequest(DateOnly? Date);

/// <summary>Enrollments and the coach roster (appendix D §2.5, §2.7, §2.8; US-14, US-17, US-18).</summary>
internal static class EnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        var enrollments = app.MapGroup("/api/v1/enrollments").WithTags("Enrollments").RequireRole(Role.SchoolAdmin);
        enrollments.MapPost("/", Enroll).Produces<EnrollmentDto>(StatusCodes.Status201Created).ProducesProblems(400, 404, 409, 422);
        enrollments.MapGet("/{id:guid}", GetEnrollment).Produces<EnrollmentDto>().ProducesProblems(404);
        enrollments.MapPost("/{id:guid}/end", EndEnrollment).Produces<EnrollmentDto>().ProducesProblems(400, 404, 409, 422);

        var admin = app.MapGroup("/api/v1").WithTags("Enrollments").RequireRole(Role.SchoolAdmin);
        admin.MapGet("/groups/{id:guid}/enrollments", ListGroupEnrollments).Produces<PageResponse<EnrollmentDto>>().ProducesProblems(400, 404);
        admin.MapGet("/students/{id:guid}/enrollments", ListStudentEnrollments).Produces<IReadOnlyList<EnrollmentDto>>().ProducesProblems(404);

        // The coach view (US-14): a separate, minimal shape (appendix D §1.9, NFR-PRV-02).
        var roster = app.MapGroup("/api/v1/groups").WithTags("Enrollments").RequireRole(Role.SchoolAdmin, Role.Coach);
        roster.MapGet("/{id:guid}/roster", GetRoster).Produces<IReadOnlyList<RosterEntryDto>>().ProducesProblems(404);

        return app;
    }

    private static async Task<IResult> Enroll(
        EnrollRequest request,
        ICommandHandler<EnrollStudentCommand, EnrollmentId> handler,
        IQueryHandler<GetEnrollmentQuery, EnrollmentDto> reader,
        CancellationToken cancellationToken)
    {
        var created = await handler.Handle(new EnrollStudentCommand(request.StudentId, request.GroupId, request.StartDate), cancellationToken);
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var enrollment = await reader.Handle(new GetEnrollmentQuery(created.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/enrollments/{created.Value.Value}", enrollment.Value);
    }

    private static async Task<IResult> GetEnrollment(Guid id, IQueryHandler<GetEnrollmentQuery, EnrollmentDto> reader, CancellationToken cancellationToken) =>
        (await reader.Handle(new GetEnrollmentQuery(EnrollmentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> EndEnrollment(
        Guid id,
        EndEnrollmentRequest request,
        ICommandHandler<EndEnrollmentCommand, Unit> handler,
        IQueryHandler<GetEnrollmentQuery, EnrollmentDto> reader,
        CancellationToken cancellationToken)
    {
        var ended = await handler.Handle(new EndEnrollmentCommand(EnrollmentId.From(id), request.Date), cancellationToken);
        return ended.IsSuccess
            ? (await reader.Handle(new GetEnrollmentQuery(EnrollmentId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(ended.Error);
    }

    private static async Task<IResult> ListGroupEnrollments(
        Guid id,
        EnrollmentStatus? status,
        int? page,
        int? pageSize,
        IQueryHandler<ListGroupEnrollmentsQuery, PagedResult<EnrollmentDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ListGroupEnrollmentsQuery(GroupId.From(id), status, EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<EnrollmentDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> ListStudentEnrollments(
        Guid id,
        IQueryHandler<ListStudentEnrollmentsQuery, IReadOnlyList<EnrollmentDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ListStudentEnrollmentsQuery(StudentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetRoster(
        Guid id,
        IQueryHandler<GetRosterQuery, IReadOnlyList<RosterEntryDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetRosterQuery(GroupId.From(id)), cancellationToken)).OkOrProblem();
}
