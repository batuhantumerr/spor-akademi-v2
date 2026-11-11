using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>
/// A payment as the parent sees it (P3-03, DEC-384): recorded payments only. Cancelled entries were bookkeeping
/// mistakes; who cancelled them and why stays with the school (DEC-007).
/// </summary>
public sealed record ParentPaymentDto(
    Guid Id,
    Guid EnrollmentId,
    Guid GroupId,
    string GroupName,
    string Period,
    MoneyDto Amount,
    PaymentMethod Method,
    DateOnly PaidOn);

/// <summary>An online payment attempt of the parent's own child (P3-03, DEC-385): where it stands and the checkout link.</summary>
public sealed record ParentOnlinePaymentDto(
    Guid Id,
    Guid EnrollmentId,
    Guid StudentId,
    Guid GroupId,
    string GroupName,
    string Period,
    MoneyDto Amount,
    OnlinePaymentStatus Status,
    string? CheckoutUrl,
    DateTimeOffset StartedAt,
    DateTimeOffset? CheckoutExpiresAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// P3-03: the child's unpaid dues of the last 24 months up to the school's current month, computed exactly like the
/// School Admin's list (<see cref="OutstandingDues"/>, so sibling discounts match). Oldest period first.
/// </summary>
public sealed record ListChildDuesQuery(StudentId StudentId) : IQuery<IReadOnlyList<OutstandingDueDto>>;

/// <summary>P3-03: the child's recorded payments, newest payment date first.</summary>
public sealed record ListChildPaymentsQuery(StudentId StudentId, PageRequest Page) : IQuery<PagedResult<ParentPaymentDto>>;

/// <summary>P3-03: an online payment attempt of one of the caller's children; any other is "not found".</summary>
public sealed record GetChildOnlinePaymentQuery(OnlinePaymentId Id) : IQuery<ParentOnlinePaymentDto>;

internal sealed class ListChildPaymentsValidator : AbstractValidator<ListChildPaymentsQuery>
{
    public ListChildPaymentsValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
    }
}

internal sealed class ListChildDuesHandler(
    IParentChildren children,
    OutstandingDues outstanding,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<ListChildDuesQuery, IReadOnlyList<OutstandingDueDto>>
{
    public async Task<Result<IReadOnlyList<OutstandingDueDto>>> Handle(ListChildDuesQuery query, CancellationToken cancellationToken)
    {
        if (!await children.IsMineAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<OutstandingDueDto>>(StudentErrors.NotFound);
        }

        var currentPeriod = YearMonth.From(await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken));
        var rows = await outstanding.ListAsync(
            OutstandingDues.HorizonStart(currentPeriod), currentPeriod, null, c => c.StudentId == query.StudentId, cancellationToken, familyOf: query.StudentId);
        return Result.Success<IReadOnlyList<OutstandingDueDto>>(rows);
    }
}

internal sealed class ListChildPaymentsHandler(IParentChildren children, IPaymentRepository payments, PaymentViews views)
    : IQueryHandler<ListChildPaymentsQuery, PagedResult<ParentPaymentDto>>
{
    public async Task<Result<PagedResult<ParentPaymentDto>>> Handle(ListChildPaymentsQuery query, CancellationToken cancellationToken)
    {
        if (!await children.IsMineAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<PagedResult<ParentPaymentDto>>(StudentErrors.NotFound);
        }

        var page = await payments.ListAsync(new PaymentFilter(query.StudentId, null, null, PaymentStatus.Recorded), query.Page, cancellationToken);
        var items = (await views.ToDtosAsync(page.Items, cancellationToken))
            .Select(p => new ParentPaymentDto(p.Id, p.EnrollmentId, p.GroupId, p.GroupName, p.Period, p.Amount, p.Method, p.PaidOn))
            .ToList();
        return Result.Success(new PagedResult<ParentPaymentDto>(items, page.Page, page.PageSize, page.TotalCount));
    }
}

internal sealed class GetChildOnlinePaymentHandler(IParentChildren children, IOnlinePaymentRepository onlinePayments, OnlinePaymentViews views)
    : IQueryHandler<GetChildOnlinePaymentQuery, ParentOnlinePaymentDto>
{
    public async Task<Result<ParentOnlinePaymentDto>> Handle(GetChildOnlinePaymentQuery query, CancellationToken cancellationToken)
    {
        var payment = await onlinePayments.FindAsync(query.Id, cancellationToken);
        if (payment is null || !await children.IsMineAsync(payment.StudentId, cancellationToken))
        {
            return Result.Failure<ParentOnlinePaymentDto>(OnlinePaymentErrors.NotFound);
        }

        var dto = (await views.ToDtosAsync([payment], cancellationToken))[0];
        return Result.Success(ParentCheckout.ToParent(dto));
    }
}

/// <summary>
/// P3-03 (DEC-385): a parent starts the online payment of their own child's due through the School Admin's flow
/// (<see cref="OnlineCheckout"/>: Idempotency-Key, one open attempt per due, the amount from <see cref="DueCharges"/>).
/// An enrollment of a student who is not the caller's child is "not found", before anything is written.
/// </summary>
public sealed class ParentCheckout(OnlineCheckout checkout, IEnrollmentDirectory enrollments, IParentChildren children)
{
    public async Task<Result<StartedOnlinePayment>> StartAsync(Guid enrollmentId, string period, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var enrollment = enrollmentId == Guid.Empty ? null : await enrollments.FindAsync(EnrollmentId.From(enrollmentId), cancellationToken);
        if (enrollment is null || !await children.IsMineAsync(enrollment.StudentId, cancellationToken))
        {
            return Result.Failure<StartedOnlinePayment>(EnrollmentErrors.NotFound);
        }

        return await checkout.StartAsync(enrollmentId, period, idempotencyKey, cancellationToken);
    }

    internal static ParentOnlinePaymentDto ToParent(OnlinePaymentDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new ParentOnlinePaymentDto(
            dto.Id,
            dto.EnrollmentId,
            dto.StudentId,
            dto.GroupId,
            dto.GroupName,
            dto.Period,
            dto.Amount,
            dto.Status,
            dto.CheckoutUrl,
            dto.StartedAt,
            dto.CheckoutExpiresAt,
            dto.CompletedAt);
    }
}
