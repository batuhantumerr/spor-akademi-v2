using Microsoft.AspNetCore.Mvc;
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

/// <summary>P3-13: why the school returns the money (required; the school's own note, never sent to the parent or provider).</summary>
internal sealed record RefundPaymentRequest(string? Reason);

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
        payments.MapPost("/{id:guid}/refund", RefundPayment)
            .Produces<PaymentDto>()
            .Produces<PaymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblems(400, 404, 409, 422);
        payments.MapPost("/{id:guid}/refund/check", CheckRefund)
            .Produces<PaymentDto>()
            .Produces<PaymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblems(404, 409, 422);

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

    /// <summary>
    /// P3-13 (DEC-418, DEC-419): refunds an online payment in full through its provider; requires an <c>Idempotency-Key</c>.
    /// 200 with the refunded payment; 202 when the provider's answer is unknown (the refund stays pending and the
    /// reconciliation job asks again); 422 <c>Refund.ProviderDeclined</c> when the provider refused. Repeating the request
    /// (same key and body) never calls the provider again: it answers by that request's own status (200, 202 or 422) with
    /// <c>Idempotent-Replayed: true</c> and that request as the payment's <c>refund</c>.
    /// </summary>
    private static async Task<IResult> RefundPayment(
        Guid id,
        [FromHeader(Name = OnlinePaymentEndpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        RefundPaymentRequest request,
        PaymentRefunds refunds,
        IQueryHandler<GetPaymentQuery, PaymentDto> reader,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var refunded = await refunds.RefundAsync(id, request.Reason, idempotencyKey, cancellationToken);
        if (refunded.IsFailure)
        {
            return ProblemResults.Problem(refunded.Error);
        }

        if (refunded.Value.Replayed)
        {
            context.Response.Headers[OnlinePaymentEndpoints.ReplayedHeader] = "true";
        }

        return await RefundResultAsync(refunded.Value, reader, cancellationToken);
    }

    /// <summary>
    /// P3-13 (DEC-418): "Sağlayıcıda kontrol et". Settles the payment's pending refund from the provider's payment detail:
    /// 200 refunded; 422 <c>Refund.ProviderDeclined</c> when the provider shows the money not returned (the request fails,
    /// a new request is the retry); 202 when the provider cannot answer. 409 when the last provider call is under a
    /// minute old; 422 <c>Refund.NotPending</c> without a pending refund.
    /// </summary>
    private static async Task<IResult> CheckRefund(
        Guid id,
        PaymentRefunds refunds,
        IQueryHandler<GetPaymentQuery, PaymentDto> reader,
        CancellationToken cancellationToken)
    {
        var checkedRefund = await refunds.CheckAsync(id, cancellationToken);
        return checkedRefund.IsSuccess
            ? await RefundResultAsync(checkedRefund.Value, reader, cancellationToken)
            : ProblemResults.Problem(checkedRefund.Error);
    }

    /// <summary>DEC-419: the answer follows the refund request's own status, with that request as the payment's refund.</summary>
    private static async Task<IResult> RefundResultAsync(RefundOutcome outcome, IQueryHandler<GetPaymentQuery, PaymentDto> reader, CancellationToken cancellationToken)
    {
        var payment = await reader.Handle(new GetPaymentQuery(outcome.PaymentId, outcome.RefundId), cancellationToken);
        return outcome.Status switch
        {
            PaymentRefundStatus.Succeeded => payment.OkOrProblem(),
            PaymentRefundStatus.Pending => payment.IsSuccess ? TypedResults.Accepted((string?)null, payment.Value) : payment.OkOrProblem(),
            _ => ProblemResults.Problem(PaymentRefundErrors.ProviderDeclined),
        };
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
