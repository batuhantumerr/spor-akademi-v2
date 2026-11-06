using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;

namespace SporAkademi.Api.Endpoints.Billing;

/// <summary>US-23: <c>period</c> is <c>YYYY-MM</c>, the amount is money as text (DEC-066), <c>paidOn</c> is the payment date.</summary>
internal sealed record RecordPaymentRequest(Guid EnrollmentId, string Period, MoneyDto Amount, PaymentMethod Method, DateOnly? PaidOn);

internal sealed record CancelPaymentRequest(string Reason);

/// <summary>Payments and dues (appendix D §2.10; US-23, US-24). School Admin only. Payments are never edited or deleted.</summary>
internal static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/v1/payments").WithTags("Payments").RequireRole(Role.SchoolAdmin);
        payments.MapGet("/", ListPayments).Produces<PageResponse<PaymentDto>>().ProducesProblems(400);
        payments.MapGet("/{id:guid}", GetPayment).Produces<PaymentDto>().ProducesProblems(404);
        payments.MapPost("/", RecordPayment).Produces<PaymentDto>(StatusCodes.Status201Created).ProducesProblems(400, 404, 422);
        payments.MapPost("/{id:guid}/cancel", CancelPayment).Produces<PaymentDto>().ProducesProblems(400, 404, 409, 422);

        var dues = app.MapGroup("/api/v1/dues").WithTags("Payments").RequireRole(Role.SchoolAdmin);
        dues.MapGet("/outstanding", ListOutstanding).Produces<PageResponse<OutstandingDueDto>>().ProducesProblems(400);

        return app;
    }

    private static async Task<IResult> ListPayments(
        Guid? studentId,
        Guid? groupId,
        string? period,
        PaymentStatus? status,
        int? page,
        int? pageSize,
        IQueryHandler<ListPaymentsQuery, PagedResult<PaymentDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ListPaymentsQuery(studentId, groupId, period, status, EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<PaymentDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetPayment(Guid id, IQueryHandler<GetPaymentQuery, PaymentDto> reader, CancellationToken cancellationToken) =>
        (await reader.Handle(new GetPaymentQuery(PaymentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> RecordPayment(
        RecordPaymentRequest request,
        ICommandHandler<RecordPaymentCommand, PaymentId> handler,
        IQueryHandler<GetPaymentQuery, PaymentDto> reader,
        CancellationToken cancellationToken)
    {
        var recorded = await handler.Handle(
            new RecordPaymentCommand(request.EnrollmentId, request.Period, request.Amount, request.Method, request.PaidOn), cancellationToken);
        if (recorded.IsFailure)
        {
            return ProblemResults.Problem(recorded.Error);
        }

        var payment = await reader.Handle(new GetPaymentQuery(recorded.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/payments/{recorded.Value.Value}", payment.Value);
    }

    private static async Task<IResult> CancelPayment(
        Guid id,
        CancelPaymentRequest request,
        ICommandHandler<CancelPaymentCommand, Unit> handler,
        IQueryHandler<GetPaymentQuery, PaymentDto> reader,
        CancellationToken cancellationToken)
    {
        var cancelled = await handler.Handle(new CancelPaymentCommand(PaymentId.From(id), request.Reason), cancellationToken);
        return cancelled.IsSuccess
            ? (await reader.Handle(new GetPaymentQuery(PaymentId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(cancelled.Error);
    }

    private static async Task<IResult> ListOutstanding(
        Guid? groupId,
        string? period,
        int? page,
        int? pageSize,
        IQueryHandler<ListOutstandingDuesQuery, PagedResult<OutstandingDueDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListOutstandingDuesQuery(groupId, period, EndpointConventions.ToPageRequest(page, pageSize)), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<OutstandingDueDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }
}
