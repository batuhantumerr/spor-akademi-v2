using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SporAkademi.Api.Endpoints.Billing;
using SporAkademi.Api.Endpoints.Tenancy;
using SporAkademi.Api.Errors;
using SporAkademi.Api.Security;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Announcements;
using SporAkademi.Application.Attendance;
using SporAkademi.Application.Billing;
using SporAkademi.Application.Enrollments;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Api.Endpoints.Parents;

/// <summary>Starts paying one of the child's dues online: the enrollment and the period (<c>YYYY-MM</c>).</summary>
internal sealed record StartChildOnlinePaymentRequest(Guid EnrollmentId, string Period);

/// <summary>
/// The parent API (P3-03, DEC-385, DEC-386): the signed-in parent's children and, per child, the weekly program,
/// attendance, dues, payments and announcements, and the online payment of a due. Parent role only. A student who is not
/// the caller's child answers <c>404 Student.NotFound</c> exactly like another school's (DEC-382).
/// </summary>
internal static class ParentEndpoints
{
    /// <summary>
    /// DEC-388: requests per parent account (the token's subject) and minute, over all parent endpoints:
    /// <c>RateLimiting:ParentPermitsPerMinute</c> (60 by default), raised only for a load test (P4-08, DEC-561).
    /// </summary>
    public const string RateLimitPolicy = "parent";

    public static IServiceCollection AddParentRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value ?? ApiRateLimiting.ClientNetwork(context.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value.ParentPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            })));

    public static IEndpointRouteBuilder MapParentEndpoints(this IEndpointRouteBuilder app)
    {
        var parent = app.MapGroup("/api/v1/parent").WithTags("Parent").RequireRole(Role.Parent).RequireRateLimiting(RateLimitPolicy);
        parent.MapGet("/children", ListChildren).Produces<IReadOnlyList<ParentChildDto>>().ProducesProblems();
        parent.MapGet("/children/{id:guid}/schedule", GetSchedule).Produces<IReadOnlyList<ChildGroupDto>>().ProducesProblems(404);
        parent.MapGet("/children/{id:guid}/attendance", GetAttendance).Produces<StudentAttendanceDto>().ProducesProblems(400, 404);
        parent.MapGet("/children/{id:guid}/dues", ListDues).Produces<IReadOnlyList<OutstandingDueDto>>().ProducesProblems(404);
        parent.MapGet("/children/{id:guid}/payments", ListPayments).Produces<PageResponse<ParentPaymentDto>>().ProducesProblems(400, 404);
        parent.MapGet("/children/{id:guid}/announcements", ListAnnouncements).Produces<PageResponse<ParentAnnouncementDto>>().ProducesProblems(400, 404);
        parent.MapPost("/online-payments", StartOnlinePayment)
            .Produces<ParentOnlinePaymentDto>(StatusCodes.Status201Created)
            .Produces<ParentOnlinePaymentDto>()
            .ProducesProblems(400, 404, 409, 422)
            .RequireFeature(FeatureCatalog.ParentOnlinePayments);
        // P4-10 (DEC-584): only starting a payment is behind the "parent-online-payments" flag; the status of a payment
        // already under way stays readable when the flag is switched off meanwhile.
        parent.MapGet("/online-payments/{id:guid}", GetOnlinePayment).Produces<ParentOnlinePaymentDto>().ProducesProblems(404);
        return app;
    }

    private static async Task<IResult> ListChildren(IQueryHandler<ListMyChildrenQuery, IReadOnlyList<ParentChildDto>> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ListMyChildrenQuery(), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetSchedule(
        Guid id,
        IQueryHandler<GetChildScheduleQuery, IReadOnlyList<ChildGroupDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetChildScheduleQuery(StudentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetAttendance(
        Guid id,
        DateOnly? from,
        DateOnly? to,
        IQueryHandler<GetChildAttendanceQuery, StudentAttendanceDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetChildAttendanceQuery(StudentId.From(id), from, to), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ListDues(
        Guid id,
        IQueryHandler<ListChildDuesQuery, IReadOnlyList<OutstandingDueDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ListChildDuesQuery(StudentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ListPayments(
        Guid id,
        int? page,
        int? pageSize,
        IQueryHandler<ListChildPaymentsQuery, PagedResult<ParentPaymentDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListChildPaymentsQuery(StudentId.From(id), EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<ParentPaymentDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> ListAnnouncements(
        Guid id,
        int? page,
        int? pageSize,
        IQueryHandler<ListChildAnnouncementsQuery, PagedResult<ParentAnnouncementDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListChildAnnouncementsQuery(StudentId.From(id), EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<ParentAnnouncementDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    /// <summary>
    /// DEC-386, like the School Admin's start (DEC-341): requires an <c>Idempotency-Key</c>; the first request answers 201,
    /// a repeat (same key and body) 200 with <c>Idempotent-Replayed: true</c> and the same attempt.
    /// </summary>
    private static async Task<IResult> StartOnlinePayment(
        [FromHeader(Name = OnlinePaymentEndpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        StartChildOnlinePaymentRequest request,
        ParentCheckout checkout,
        IQueryHandler<GetChildOnlinePaymentQuery, ParentOnlinePaymentDto> reader,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var started = await checkout.StartAsync(request.EnrollmentId, request.Period, idempotencyKey, cancellationToken);
        if (started.IsFailure)
        {
            return ProblemResults.Problem(started.Error);
        }

        var payment = await reader.Handle(new GetChildOnlinePaymentQuery(started.Value.Id), cancellationToken);
        if (started.Value.Replayed)
        {
            context.Response.Headers[OnlinePaymentEndpoints.ReplayedHeader] = "true";
            return payment.OkOrProblem();
        }

        return payment.IsSuccess
            ? TypedResults.Created($"/api/v1/parent/online-payments/{started.Value.Id.Value}", payment.Value)
            : ProblemResults.Problem(payment.Error);
    }

    private static async Task<IResult> GetOnlinePayment(
        Guid id,
        IQueryHandler<GetChildOnlinePaymentQuery, ParentOnlinePaymentDto> reader,
        CancellationToken cancellationToken) =>
        (await reader.Handle(new GetChildOnlinePaymentQuery(OnlinePaymentId.From(id)), cancellationToken)).OkOrProblem();
}
