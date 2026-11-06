using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>US-23: a cash or bank transfer payment for an enrollment and period (<c>YYYY-MM</c>), paid on a date up to today.</summary>
public sealed record RecordPaymentCommand(Guid EnrollmentId, string Period, MoneyDto Amount, PaymentMethod Method, DateOnly? PaidOn) : ICommand<PaymentId>;

/// <summary>US-23: cancels a wrong entry with a reason. Payments are never edited or deleted.</summary>
public sealed record CancelPaymentCommand(PaymentId Id, string Reason) : ICommand<Unit>;

public sealed record GetPaymentQuery(PaymentId Id) : IQuery<PaymentDto>;

public sealed record ListPaymentsQuery(Guid? StudentId, Guid? GroupId, string? Period, PaymentStatus? Status, PageRequest Page) : IQuery<PagedResult<PaymentDto>>;

internal static class BillingInput
{
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    public static bool IsPeriod(string? text) => text is not null && YearMonth.Parse(text).IsSuccess;

    public static bool IsSupportedDate(DateOnly? date) => date is not { } d || d.Year is >= MinYear and <= MaxYear;
}

internal sealed class RecordPaymentValidator : AbstractValidator<RecordPaymentCommand>
{
    public RecordPaymentValidator()
    {
        RuleFor(c => c.EnrollmentId).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code);
        RuleFor(c => c.Period).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(BillingInput.IsPeriod).WithErrorCode(YearMonth.Invalid.Code);
        RuleFor(c => c.Amount).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(a => a.ToMoney().IsSuccess).WithErrorCode(Money.Invalid.Code)
            .Must(a => a.ToMoney().Value.Amount > 0).WithErrorCode(PaymentErrors.InvalidAmount.Code);
        RuleFor(c => c.Method).IsInEnum().WithErrorCode(CommonErrors.FieldInvalid.Code);
        RuleFor(c => c.PaidOn).NotNull().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(BillingInput.IsSupportedDate).WithErrorCode(CommonErrors.FieldInvalid.Code);
    }
}

internal sealed class CancelPaymentValidator : AbstractValidator<CancelPaymentCommand>
{
    public CancelPaymentValidator() =>
        RuleFor(c => c.Reason).Cascade(CascadeMode.Stop)
            .Must(r => !string.IsNullOrWhiteSpace(r)).WithErrorCode(PaymentErrors.CancelReasonRequired.Code)
            .Must(r => r.Trim().Length <= Payment.CancelReasonMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
}

internal sealed class ListPaymentsValidator : AbstractValidator<ListPaymentsQuery>
{
    public ListPaymentsValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
        RuleFor(q => q.Period).Must(BillingInput.IsPeriod).WithErrorCode(YearMonth.Invalid.Code).When(q => q.Period is not null);
        RuleFor(q => q.Status).IsInEnum().WithErrorCode("Field.Invalid");
    }
}

internal sealed class RecordPaymentHandler(
    IPaymentRepository payments,
    IEnrollmentDirectory enrollments,
    IGroupFeeSchedule fees,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : ICommandHandler<RecordPaymentCommand, PaymentId>
{
    public async Task<Result<PaymentId>> Handle(RecordPaymentCommand command, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var paidOn = command.PaidOn!.Value;
        if (paidOn > await tenancy.TodayAsync(tenant, cancellationToken))
        {
            return Result.Failure<PaymentId>(ValidationErrors.ForField("paidOn", CommonErrors.FieldInvalid));
        }

        // DEC-184: the enrollment row stays locked until commit, so ending it concurrently waits for this payment,
        // and two payments of one enrollment are serialized (the period total below stays exact).
        var enrollment = await enrollments.FindLockedAsync(EnrollmentId.From(command.EnrollmentId), cancellationToken);
        if (enrollment is null)
        {
            return Result.Failure<PaymentId>(EnrollmentErrors.NotFound);
        }

        var period = YearMonth.Parse(command.Period).Value;
        var amount = command.Amount.ToMoney().Value;

        // A period's recorded total stays within the money bound, so totals and reports never overflow.
        if (await payments.RecordedTotalAsync(enrollment.Id, period, cancellationToken) + amount.Amount > Money.MaxAmount)
        {
            return Result.Failure<PaymentId>(ValidationErrors.ForField("amount", Money.Invalid));
        }

        var groupFees = await fees.GetFeesAsync([enrollment.GroupId], cancellationToken);
        var feeDefined = groupFees.TryGetValue(enrollment.GroupId, out var rates)
            && FeeRate.For(rates.Select(r => new FeeRate(r.EffectiveFrom, r.Amount)), period) is not null;

        var recorded = Payment.Record(
            tenant,
            new PaidEnrollment(enrollment.Id, enrollment.StudentId, enrollment.GroupId, enrollment.StartDate, enrollment.EndDate),
            period,
            amount,
            command.Method,
            paidOn,
            feeDefined);
        if (recorded.IsFailure)
        {
            return Result.Failure<PaymentId>(recorded.Error);
        }

        payments.Add(recorded.Value);
        return Result.Success(recorded.Value.Id);
    }
}

internal sealed class CancelPaymentHandler(IPaymentRepository payments, ICurrentUser currentUser, TimeProvider timeProvider)
    : ICommandHandler<CancelPaymentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(CancelPaymentCommand command, CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(command.Id, cancellationToken);
        if (payment is null)
        {
            return Result.Failure<Unit>(PaymentErrors.NotFound);
        }

        var cancelledBy = UserId.From(currentUser.UserId ?? throw new InvalidOperationException("Cancelling needs a signed-in user."));
        var cancelled = payment.Cancel(command.Reason, cancelledBy, timeProvider.GetUtcNow());
        return cancelled.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(cancelled.Error);
    }
}

/// <summary>Adds student and group names from the Students and Groups contracts, in batches.</summary>
internal sealed class PaymentViews(IStudentDirectory students, IGroupDirectory groups)
{
    public async Task<IReadOnlyList<PaymentDto>> ToDtosAsync(IReadOnlyList<Payment> payments, CancellationToken cancellationToken)
    {
        var studentNames = await students.GetManyAsync([.. payments.Select(p => p.StudentId.Value).Distinct()], cancellationToken);
        var groupNames = await groups.GetManyAsync([.. payments.Select(p => p.GroupId.Value).Distinct()], cancellationToken);
        return
        [
            .. payments.Select(p =>
            {
                var student = studentNames.GetValueOrDefault(p.StudentId.Value);
                return new PaymentDto(
                    p.Id.Value,
                    p.EnrollmentId.Value,
                    p.StudentId.Value,
                    student?.FirstName ?? string.Empty,
                    student?.LastName ?? string.Empty,
                    p.GroupId.Value,
                    groupNames.GetValueOrDefault(p.GroupId.Value)?.Name ?? string.Empty,
                    p.Period.ToString(),
                    MoneyDto.From(p.Amount),
                    p.Method,
                    p.PaidOn,
                    p.Status,
                    p.CancelReason,
                    p.CancelledBy?.Value,
                    p.CancelledAt);
            }),
        ];
    }
}

internal sealed class GetPaymentHandler(IPaymentRepository payments, PaymentViews views) : IQueryHandler<GetPaymentQuery, PaymentDto>
{
    public async Task<Result<PaymentDto>> Handle(GetPaymentQuery query, CancellationToken cancellationToken) =>
        await payments.FindAsync(query.Id, cancellationToken) is { } payment
            ? Result.Success((await views.ToDtosAsync([payment], cancellationToken))[0])
            : Result.Failure<PaymentDto>(PaymentErrors.NotFound);
}

internal sealed class ListPaymentsHandler(IPaymentRepository payments, PaymentViews views)
    : IQueryHandler<ListPaymentsQuery, PagedResult<PaymentDto>>
{
    public async Task<Result<PagedResult<PaymentDto>>> Handle(ListPaymentsQuery query, CancellationToken cancellationToken)
    {
        var filter = new PaymentFilter(
            query.StudentId is { } student ? StudentId.From(student) : null,
            query.GroupId is { } group ? GroupId.From(group) : null,
            query.Period is { } period ? YearMonth.Parse(period).Value : null,
            query.Status);
        var page = await payments.ListAsync(filter, query.Page, cancellationToken);
        return Result.Success(new PagedResult<PaymentDto>(await views.ToDtosAsync(page.Items, cancellationToken), page.Page, page.PageSize, page.TotalCount));
    }
}
