using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>An online payment attempt for the School Admin (P2-F1): the due, the amount, where it stands and the checkout link.</summary>
public sealed record OnlinePaymentDto(
    Guid Id,
    Guid EnrollmentId,
    Guid StudentId,
    string StudentFirstName,
    string StudentLastName,
    Guid GroupId,
    string GroupName,
    string Period,
    MoneyDto Amount,
    string Provider,
    OnlinePaymentStatus Status,
    string? CheckoutUrl,
    DateTimeOffset StartedAt,
    DateTimeOffset? CheckoutExpiresAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    Guid? PaymentId,
    bool Overpaid);

public sealed record OnlinePaymentFilter(EnrollmentId? EnrollmentId, YearMonth? Period, OnlinePaymentStatus? Status);

/// <summary>Online payments of the current school (tenant filter applies), and the notifications already applied.</summary>
public interface IOnlinePaymentRepository
{
    /// <summary>Tracked.</summary>
    Task<OnlinePayment?> GetAsync(OnlinePaymentId id, CancellationToken cancellationToken);

    Task<OnlinePayment?> FindAsync(OnlinePaymentId id, CancellationToken cancellationToken);

    Task<OnlinePayment?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The attempt of the due that is still <c>Created</c> or <c>Pending</c> (at most one, by a unique index).</summary>
    Task<OnlinePayment?> FindOpenAsync(EnrollmentId enrollmentId, YearMonth period, CancellationToken cancellationToken);

    /// <summary>Marks an open attempt <c>Expired</c> at once (in the current transaction), so a new attempt can take its place.</summary>
    Task ExpireAsync(OnlinePaymentId id, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(OnlinePayment payment);

    /// <summary>Newest first.</summary>
    Task<PagedResult<OnlinePayment>> ListAsync(OnlinePaymentFilter filter, PageRequest page, CancellationToken cancellationToken);

    /// <summary>Open attempts (with a checkout) started before <paramref name="startedBefore"/>, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<OnlinePayment>> ListOpenStartedBeforeAsync(DateTimeOffset startedBefore, int max, CancellationToken cancellationToken);

    Task<bool> NotificationExistsAsync(string provider, string eventKey, CancellationToken cancellationToken);

    void Add(PaymentNotification notification);
}

/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header (DEC-341).</param>
public sealed record StartOnlinePaymentCommand(Guid EnrollmentId, string Period, string? IdempotencyKey) : ICommand<StartedOnlinePayment>;

/// <param name="Replayed">An earlier request with the same key and body created it; nothing new was started.</param>
public sealed record StartedOnlinePayment(OnlinePaymentId Id, bool Replayed);

/// <summary>Stores the provider's answer to "open a checkout": the opening, or the failure code.</summary>
public sealed record OpenOnlineCheckoutCommand(OnlinePaymentId Id, CheckoutOpening? Opening, string? FailureCode) : ICommand<Unit>;

/// <summary>Applies the provider's authoritative state of an online payment, once per notification key.</summary>
public sealed record ApplyProviderStateCommand(OnlinePaymentId Id, string Provider, ProviderPaymentState State, string? EventKey) : ICommand<AppliedProviderState>;

/// <param name="Changed">The state changed (a payment was recorded or the attempt failed); <c>false</c> for repeats.</param>
public sealed record AppliedProviderState(bool Changed, OnlinePaymentStatus Status);

/// <summary>Expires an open attempt whose checkout lifetime passed (reconciliation job, DEC-343).</summary>
public sealed record ExpireOnlinePaymentCommand(OnlinePaymentId Id) : ICommand<bool>;

public sealed record GetOnlinePaymentQuery(OnlinePaymentId Id) : IQuery<OnlinePaymentDto>;

public sealed record ListOnlinePaymentsQuery(Guid? EnrollmentId, string? Period, OnlinePaymentStatus? Status, PageRequest Page) : IQuery<PagedResult<OnlinePaymentDto>>;

internal static partial class IdempotencyKeys
{
    /// <summary>Printable token characters only, so a key is never free text (it may be logged as an id).</summary>
    [GeneratedRegex("^[A-Za-z0-9._:-]{8,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    public static partial Regex Pattern();

    /// <summary>SHA-256 of the canonical request, so a key reused with another body is recognised (DEC-341).</summary>
    public static string RequestHash(Guid enrollmentId, string period) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"start-online-payment|{enrollmentId:D}|{period}")));
}

internal sealed class StartOnlinePaymentValidator : AbstractValidator<StartOnlinePaymentCommand>
{
    public StartOnlinePaymentValidator()
    {
        RuleFor(c => c.IdempotencyKey).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(IdempotencyErrors.KeyRequired.Code)
            .Must(k => IdempotencyKeys.Pattern().IsMatch(k!)).WithErrorCode(IdempotencyErrors.KeyInvalid.Code);
        RuleFor(c => c.EnrollmentId).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code);
        RuleFor(c => c.Period).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(BillingInput.IsPeriod).WithErrorCode(YearMonth.Invalid.Code);
    }
}

internal sealed class ListOnlinePaymentsValidator : AbstractValidator<ListOnlinePaymentsQuery>
{
    public ListOnlinePaymentsValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
        RuleFor(q => q.Period).Must(BillingInput.IsPeriod).WithErrorCode(YearMonth.Invalid.Code).When(q => q.Period is not null);
        RuleFor(q => q.Status).IsInEnum().WithErrorCode("Field.Invalid");
    }
}

/// <summary>
/// Creates the attempt for the due's remaining amount (DEC-341, DEC-342). Idempotent per key: the same key and body
/// returns the same attempt, the same key with another body is refused. At most one open attempt per due; an open one
/// whose checkout lifetime passed is expired and replaced.
/// </summary>
internal sealed class StartOnlinePaymentHandler(
    IOnlinePaymentRepository onlinePayments,
    IPaymentRepository payments,
    IEnrollmentDirectory enrollments,
    DueCharges dueCharges,
    ITenancyModule tenancy,
    IPaymentProviders providers,
    ICurrentUser currentUser,
    TimeProvider clock) : ICommandHandler<StartOnlinePaymentCommand, StartedOnlinePayment>
{
    public async Task<Result<StartedOnlinePayment>> Handle(StartOnlinePaymentCommand command, CancellationToken cancellationToken)
    {
        if (providers.Active is not { } provider)
        {
            return Result.Failure<StartedOnlinePayment>(OnlinePaymentErrors.Disabled);
        }

        var key = command.IdempotencyKey!;
        var hash = IdempotencyKeys.RequestHash(command.EnrollmentId, command.Period);
        if (await onlinePayments.FindByIdempotencyKeyAsync(key, cancellationToken) is { } earlier)
        {
            return string.Equals(earlier.RequestHash, hash, StringComparison.Ordinal)
                ? Result.Success(new StartedOnlinePayment(earlier.Id, Replayed: true))
                : Result.Failure<StartedOnlinePayment>(IdempotencyErrors.KeyReused);
        }

        var tenant = currentUser.RequireTenant();
        var period = YearMonth.Parse(command.Period).Value;
        if (period > YearMonth.From(await tenancy.TodayAsync(tenant, cancellationToken)))
        {
            return Result.Failure<StartedOnlinePayment>(OnlinePaymentErrors.PeriodInFuture);
        }

        // DEC-184: like a manual payment, the enrollment row stays locked until commit, so attempts of one due and
        // payments recorded meanwhile are serialized and the remaining amount below stays exact.
        var enrollment = await enrollments.FindLockedAsync(EnrollmentId.From(command.EnrollmentId), cancellationToken);
        if (enrollment is null)
        {
            return Result.Failure<StartedOnlinePayment>(EnrollmentErrors.NotFound);
        }

        // DEC-341: a concurrent request with the same key may have committed while this one waited for the lock;
        // reading again under the lock turns it into a replay instead of "checkout in progress".
        if (await onlinePayments.FindByIdempotencyKeyAsync(key, cancellationToken) is { } concurrent)
        {
            return string.Equals(concurrent.RequestHash, hash, StringComparison.Ordinal)
                ? Result.Success(new StartedOnlinePayment(concurrent.Id, Replayed: true))
                : Result.Failure<StartedOnlinePayment>(IdempotencyErrors.KeyReused);
        }

        var now = clock.GetUtcNow();
        if (await onlinePayments.FindOpenAsync(enrollment.Id, period, cancellationToken) is { } open)
        {
            if (!open.HasExpired(now, providers.CheckoutLifetime))
            {
                return Result.Failure<StartedOnlinePayment>(OnlinePaymentErrors.CheckoutInProgress);
            }

            await onlinePayments.ExpireAsync(open.Id, now, cancellationToken);
        }

        // DEC-373: the amount is what the due still owes under the fee rules (e.g. after a sibling discount).
        var charge = await dueCharges.ForEnrollmentAsync(enrollment, period, cancellationToken);
        if (charge is null)
        {
            return Result.Failure<StartedOnlinePayment>(
                DueCalculator.WasActiveIn(enrollment.StartDate, enrollment.EndDate, period)
                    ? PaymentErrors.FeeNotDefined
                    : PaymentErrors.EnrollmentNotActiveInPeriod);
        }

        var paid = await payments.RecordedTotalAsync(enrollment.Id, period, cancellationToken);
        var remaining = charge.Fee.Amount - paid;
        var started = OnlinePayment.Start(
            tenant,
            new PaidEnrollment(enrollment.Id, enrollment.StudentId, enrollment.GroupId, enrollment.StartDate, enrollment.EndDate),
            period,
            remaining > 0 ? Money.Try(remaining) : Money.Zero,
            provider.Name,
            key,
            hash,
            now);
        if (started.IsFailure)
        {
            return Result.Failure<StartedOnlinePayment>(started.Error);
        }

        onlinePayments.Add(started.Value);
        return Result.Success(new StartedOnlinePayment(started.Value.Id, Replayed: false));
    }
}

internal sealed class OpenOnlineCheckoutHandler(IOnlinePaymentRepository onlinePayments, TimeProvider clock)
    : ICommandHandler<OpenOnlineCheckoutCommand, Unit>
{
    public async Task<Result<Unit>> Handle(OpenOnlineCheckoutCommand command, CancellationToken cancellationToken)
    {
        var payment = await onlinePayments.GetAsync(command.Id, cancellationToken);
        if (payment is null)
        {
            return Result.Failure<Unit>(OnlinePaymentErrors.NotFound);
        }

        if (command.Opening is { } opening)
        {
            var opened = payment.OpenCheckout(opening.Token, opening.CheckoutUrl, opening.ExpiresAt);
            return opened.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(opened.Error);
        }

        payment.Fail(command.FailureCode ?? "CheckoutFailed", clock.GetUtcNow());
        return Result.Success(Unit.Value);
    }
}

/// <summary>
/// The payment state machine's single entry for provider results (DEC-342, DEC-343). A notification key already
/// applied is a no-op; a success records exactly one payment (and is a no-op when repeated); a failure never undoes a
/// success. Concurrent deliveries are serialized by the enrollment lock, the row's <c>xmin</c> and the unique indexes.
/// A notification is stored as applied only with a final result (success or failure): while the provider still reports
/// the payment as pending, the same webhook delivered again is applied normally.
/// </summary>
internal sealed partial class ApplyProviderStateHandler(
    IOnlinePaymentRepository onlinePayments,
    IPaymentRepository payments,
    IEnrollmentDirectory enrollments,
    DueCharges dueCharges,
    ITenancyModule tenancy,
    TimeProvider clock,
    ILogger<ApplyProviderStateHandler> logger) : ICommandHandler<ApplyProviderStateCommand, AppliedProviderState>
{
    public async Task<Result<AppliedProviderState>> Handle(ApplyProviderStateCommand command, CancellationToken cancellationToken)
    {
        var payment = await onlinePayments.GetAsync(command.Id, cancellationToken);
        if (payment is null || !string.Equals(payment.Provider, command.Provider, StringComparison.Ordinal))
        {
            return Result.Failure<AppliedProviderState>(OnlinePaymentErrors.NotFound);
        }

        if (command.EventKey is { } eventKey && await onlinePayments.NotificationExistsAsync(command.Provider, eventKey, cancellationToken))
        {
            return Result.Success(new AppliedProviderState(false, payment.Status));
        }

        var state = command.State;
        switch (state.Outcome)
        {
            case ProviderPaymentOutcome.Succeeded when state.ProviderPaymentId is { } providerPaymentId && state.PaidAmount is { } paid:
                // Serializes with manual payments and with ending the enrollment (DEC-184).
                var enrollment = await enrollments.FindLockedAsync(payment.EnrollmentId, cancellationToken);
                var paidOn = await tenancy.TodayAsync(payment.TenantId, cancellationToken);
                var succeeded = payment.Succeed(new ProviderCharge(providerPaymentId, paid, clock.GetUtcNow(), paidOn));
                if (succeeded.IsFailure)
                {
                    LogMismatch(logger, payment.Id.Value, command.Provider);
                    return Result.Failure<AppliedProviderState>(succeeded.Error);
                }

                RecordNotification(command, payment);
                if (succeeded.Value is not { } recorded)
                {
                    return Result.Success(new AppliedProviderState(false, payment.Status));
                }

                payments.Add(recorded);
                await FlagOverpaymentAsync(payment, enrollment, recorded, cancellationToken);
                return Result.Success(new AppliedProviderState(true, payment.Status));

            case ProviderPaymentOutcome.Succeeded:
                return Result.Failure<AppliedProviderState>(OnlinePaymentErrors.ProviderMismatch);

            case ProviderPaymentOutcome.Failed:
                RecordNotification(command, payment);
                var failed = payment.Fail(state.FailureCode ?? "ProviderFailure", clock.GetUtcNow());
                return Result.Success(new AppliedProviderState(failed, payment.Status));

            default:
                return Result.Success(new AppliedProviderState(false, payment.Status));
        }
    }

    private void RecordNotification(ApplyProviderStateCommand command, OnlinePayment payment)
    {
        if (command.EventKey is { } eventKey)
        {
            onlinePayments.Add(PaymentNotification.Receive(payment, eventKey, clock.GetUtcNow()));
        }
    }

    /// <summary>
    /// DEC-342: a success is always credited, but when it takes the due above what it owes (a late success after a cash
    /// payment or a second paid attempt; DEC-373: the owed fee after the fee rules) the attempt is flagged and a warning
    /// (ids only) asks for a refund by hand.
    /// </summary>
    private async Task FlagOverpaymentAsync(OnlinePayment payment, EnrollmentInfo? enrollment, Payment recorded, CancellationToken cancellationToken)
    {
        var earlier = await payments.RecordedTotalAsync(payment.EnrollmentId, payment.Period, cancellationToken);
        var charge = enrollment is null ? null : await dueCharges.ForEnrollmentAsync(enrollment, payment.Period, cancellationToken);
        if (charge is not null && earlier + recorded.Amount.Amount > charge.Fee.Amount)
        {
            payment.FlagOverpaid();
            LogOverpaid(logger, payment.Id.Value, payment.EnrollmentId.Value, payment.Period.ToString(), recorded.Id.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Online payment {OnlinePaymentId} overpaid enrollment {EnrollmentId} for {Period} (payment {PaymentId}); refund by hand")]
    private static partial void LogOverpaid(ILogger logger, Guid onlinePaymentId, Guid enrollmentId, string period, Guid paymentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Provider} charge for online payment {OnlinePaymentId} does not match it (amount or charge id)")]
    private static partial void LogMismatch(ILogger logger, Guid onlinePaymentId, string provider);
}

internal sealed class ExpireOnlinePaymentHandler(IOnlinePaymentRepository onlinePayments, IPaymentProviders providers, TimeProvider clock)
    : ICommandHandler<ExpireOnlinePaymentCommand, bool>
{
    public async Task<Result<bool>> Handle(ExpireOnlinePaymentCommand command, CancellationToken cancellationToken) =>
        await onlinePayments.GetAsync(command.Id, cancellationToken) is { } payment
            ? Result.Success(payment.Expire(clock.GetUtcNow(), providers.CheckoutLifetime))
            : Result.Failure<bool>(OnlinePaymentErrors.NotFound);
}

/// <summary>Adds student and group names from the Students and Groups contracts, in batches.</summary>
internal sealed class OnlinePaymentViews(IStudentDirectory students, IGroupDirectory groups, IPaymentProviders providers, TimeProvider clock)
{
    public async Task<IReadOnlyList<OnlinePaymentDto>> ToDtosAsync(IReadOnlyList<OnlinePayment> items, CancellationToken cancellationToken)
    {
        var studentNames = await students.GetManyAsync([.. items.Select(p => p.StudentId.Value).Distinct()], cancellationToken);
        var groupNames = await groups.GetManyAsync([.. items.Select(p => p.GroupId.Value).Distinct()], cancellationToken);
        var now = clock.GetUtcNow();
        return
        [
            .. items.Select(p =>
            {
                var student = studentNames.GetValueOrDefault(p.StudentId.Value);
                // An open attempt past its checkout lifetime is shown as expired and its link is no longer offered;
                // the reconciliation job (or the next start) stores the expiry.
                var expired = p.HasExpired(now, providers.CheckoutLifetime);
                return new OnlinePaymentDto(
                    p.Id.Value,
                    p.EnrollmentId.Value,
                    p.StudentId.Value,
                    student?.FirstName ?? string.Empty,
                    student?.LastName ?? string.Empty,
                    p.GroupId.Value,
                    groupNames.GetValueOrDefault(p.GroupId.Value)?.Name ?? string.Empty,
                    p.Period.ToString(),
                    MoneyDto.From(p.Amount),
                    p.Provider,
                    expired ? OnlinePaymentStatus.Expired : p.Status,
                    p.IsOpen && !expired ? p.CheckoutUrl : null,
                    p.StartedAt,
                    p.CheckoutExpiresAt,
                    p.CompletedAt,
                    p.FailureCode,
                    p.PaymentId?.Value,
                    p.Overpaid);
            }),
        ];
    }
}

internal sealed class GetOnlinePaymentHandler(IOnlinePaymentRepository onlinePayments, OnlinePaymentViews views)
    : IQueryHandler<GetOnlinePaymentQuery, OnlinePaymentDto>
{
    public async Task<Result<OnlinePaymentDto>> Handle(GetOnlinePaymentQuery query, CancellationToken cancellationToken) =>
        await onlinePayments.FindAsync(query.Id, cancellationToken) is { } payment
            ? Result.Success((await views.ToDtosAsync([payment], cancellationToken))[0])
            : Result.Failure<OnlinePaymentDto>(OnlinePaymentErrors.NotFound);
}

internal sealed class ListOnlinePaymentsHandler(IOnlinePaymentRepository onlinePayments, OnlinePaymentViews views)
    : IQueryHandler<ListOnlinePaymentsQuery, PagedResult<OnlinePaymentDto>>
{
    public async Task<Result<PagedResult<OnlinePaymentDto>>> Handle(ListOnlinePaymentsQuery query, CancellationToken cancellationToken)
    {
        var filter = new OnlinePaymentFilter(
            query.EnrollmentId is { } enrollment ? EnrollmentId.From(enrollment) : null,
            query.Period is { } period ? YearMonth.Parse(period).Value : null,
            query.Status);
        var page = await onlinePayments.ListAsync(filter, query.Page, cancellationToken);
        return Result.Success(new PagedResult<OnlinePaymentDto>(await views.ToDtosAsync(page.Items, cancellationToken), page.Page, page.PageSize, page.TotalCount));
    }
}
