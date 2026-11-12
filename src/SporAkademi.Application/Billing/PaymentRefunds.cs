using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>
/// The latest refund request of a payment, for the School Admin (P3-13): where it stands, why and when. The reason is the
/// school's own note and never reaches the parent (DEC-423).
/// </summary>
public sealed record PaymentRefundDto(
    Guid Id,
    PaymentRefundStatus Status,
    string Reason,
    Guid RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? FailureCode,
    int Attempts,
    bool NeedsReview,
    Guid? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

/// <summary>Refund requests of the current school (tenant filter applies). There is deliberately no delete.</summary>
public interface IPaymentRefundRepository
{
    /// <summary>Tracked.</summary>
    Task<PaymentRefund?> GetAsync(PaymentRefundId id, CancellationToken cancellationToken);

    Task<PaymentRefund?> FindAsync(PaymentRefundId id, CancellationToken cancellationToken);

    Task<PaymentRefund?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The payment's request that is <c>Pending</c> or <c>Succeeded</c> (at most one, by a unique index).</summary>
    Task<PaymentRefund?> FindActiveAsync(PaymentId paymentId, CancellationToken cancellationToken);

    /// <summary>The newest request of each payment that has one.</summary>
    Task<IReadOnlyDictionary<PaymentId, PaymentRefund>> LatestOfPaymentsAsync(IReadOnlyCollection<PaymentId> paymentIds, CancellationToken cancellationToken);

    /// <summary>
    /// Pending requests not marked for review whose last provider call (or the request) is before <paramref name="before"/>,
    /// oldest first.
    /// </summary>
    Task<IReadOnlyList<PaymentRefund>> ListPendingSinceBeforeAsync(DateTimeOffset before, int max, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the next provider call of a pending request (DEC-419): one conditional <c>UPDATE</c>, committed at once,
    /// that counts the attempt only if nobody else did since it was read (<paramref name="expectedAttempts"/>) and, when
    /// <paramref name="lastAttemptBefore"/> is given, the last call (or the request) is older than it.
    /// </summary>
    /// <returns><c>true</c> when this caller may call the provider.</returns>
    Task<bool> TryClaimAttemptAsync(
        PaymentRefundId id,
        int expectedAttempts,
        DateTimeOffset now,
        DateTimeOffset? lastAttemptBefore,
        CancellationToken cancellationToken);

    /// <summary>
    /// Per enrollment and period, the amount of recorded payments whose refund is pending: money that may already be on its
    /// way back, so it is not counted as an overpayment that covers the due (DEC-420).
    /// </summary>
    Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> PendingRefundTotalsAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        CancellationToken cancellationToken);

    void Add(PaymentRefund refund);
}

/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header (DEC-419).</param>
public sealed record RequestRefundCommand(PaymentId PaymentId, string? Reason, string? IdempotencyKey) : ICommand<RequestedRefund>;

/// <param name="Replayed">An earlier request with the same key and body created it; nothing new was requested.</param>
public sealed record RequestedRefund(PaymentRefundId Id, bool Replayed);

/// <summary>How a provider answer settles a pending request (DEC-417, DEC-418).</summary>
public enum RefundResolution
{
    Succeeded,
    Failed,
    NeedsReview,
}

/// <summary>Stores a settled provider answer of a refund request (the attempt was counted when it was claimed).</summary>
public sealed record ApplyRefundResultCommand(PaymentRefundId Id, RefundResolution Resolution, string? ProviderRefundId, string? FailureCode)
    : ICommand<PaymentRefundStatus>;

/// <summary>DEC-418: a School Admin settles the payment's refund in review by hand, with a required internal note.</summary>
public sealed record ResolveRefundCommand(PaymentId PaymentId, bool Refunded, string? Note) : ICommand<PaymentRefundId>;

internal sealed class ResolveRefundValidator : AbstractValidator<ResolveRefundCommand>
{
    public ResolveRefundValidator() =>
        RuleFor(c => c.Note).Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithErrorCode(PaymentRefundErrors.ResolutionNoteRequired.Code)
            .Must(n => n!.Trim().Length <= PaymentRefund.ReasonMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
}

/// <summary>
/// Settles a refund in review by hand (DEC-418), under the enrollment lock. Audited: who, when and the note are stored on
/// the request and an information log records the ids and the outcome (never the note).
/// </summary>
internal sealed partial class ResolveRefundHandler(
    IPaymentRefundRepository refunds,
    IPaymentRepository payments,
    IEnrollmentDirectory enrollments,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ResolveRefundHandler> logger) : ICommandHandler<ResolveRefundCommand, PaymentRefundId>
{
    public async Task<Result<PaymentRefundId>> Handle(ResolveRefundCommand command, CancellationToken cancellationToken)
    {
        var payment = await payments.FindAsync(command.PaymentId, cancellationToken);
        if (payment is null)
        {
            return Result.Failure<PaymentRefundId>(PaymentErrors.NotFound);
        }

        await enrollments.FindLockedAsync(payment.EnrollmentId, cancellationToken);
        if (await refunds.FindActiveAsync(payment.Id, cancellationToken) is not { Status: PaymentRefundStatus.Pending } active)
        {
            return Result.Failure<PaymentRefundId>(PaymentRefundErrors.NotInReview);
        }

        var refund = await refunds.GetAsync(active.Id, cancellationToken);
        var tracked = await payments.GetAsync(payment.Id, cancellationToken);
        var by = UserId.From(currentUser.UserId ?? throw new InvalidOperationException("Settling a refund needs a signed-in user."));
        var resolved = refund!.ResolveManually(tracked!, command.Refunded, command.Note!, by, clock.GetUtcNow());
        if (resolved.IsFailure)
        {
            return Result.Failure<PaymentRefundId>(resolved.Error);
        }

        LogResolved(logger, refund.Id.Value, payment.Id.Value, by.Value, refund.Status);
        return Result.Success(refund.Id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} of payment {PaymentId} settled by hand by user {UserId}: {Status}")]
    private static partial void LogResolved(ILogger logger, Guid refundId, Guid paymentId, Guid userId, PaymentRefundStatus status);
}

/// <summary>Where a refund ended up after the request (and, unless replayed, the provider call).</summary>
public sealed record RefundOutcome(PaymentRefundId RefundId, PaymentId PaymentId, PaymentRefundStatus Status, bool Replayed);

internal static class RefundKeys
{
    /// <summary>SHA-256 of the canonical request, so a key reused for another payment or reason is recognised (DEC-419).</summary>
    public static string RequestHash(Guid paymentId, string reason) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"refund-payment|{paymentId:D}|{reason.Trim()}")));
}

internal sealed class RequestRefundValidator : AbstractValidator<RequestRefundCommand>
{
    public RequestRefundValidator()
    {
        RuleFor(c => c.IdempotencyKey).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(IdempotencyErrors.KeyRequired.Code)
            .Must(k => IdempotencyKeys.Pattern().IsMatch(k!)).WithErrorCode(IdempotencyErrors.KeyInvalid.Code);
        RuleFor(c => c.Reason).Cascade(CascadeMode.Stop)
            .Must(r => !string.IsNullOrWhiteSpace(r)).WithErrorCode(PaymentRefundErrors.ReasonRequired.Code)
            .Must(r => r!.Trim().Length <= PaymentRefund.ReasonMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
    }
}

/// <summary>
/// Records a refund request (DEC-419). Idempotent per key: the same key and body returns the same request, the same key
/// with another body is refused. Under the enrollment's row lock (DEC-184, the order every change of a due takes), so
/// two admins or a double click cannot both request a refund; the partial unique index of active requests backs it up.
/// </summary>
internal sealed class RequestRefundHandler(
    IPaymentRefundRepository refunds,
    IPaymentRepository payments,
    IOnlinePaymentRepository onlinePayments,
    IEnrollmentDirectory enrollments,
    IPaymentProviders providers,
    ICurrentUser currentUser,
    TimeProvider clock) : ICommandHandler<RequestRefundCommand, RequestedRefund>
{
    public async Task<Result<RequestedRefund>> Handle(RequestRefundCommand command, CancellationToken cancellationToken)
    {
        var key = command.IdempotencyKey!;
        var hash = RefundKeys.RequestHash(command.PaymentId.Value, command.Reason!);
        if (Replay(await refunds.FindByIdempotencyKeyAsync(key, cancellationToken), hash) is { } earlier)
        {
            return earlier;
        }

        var payment = await payments.FindAsync(command.PaymentId, cancellationToken);
        if (payment is null)
        {
            return Result.Failure<RequestedRefund>(PaymentErrors.NotFound);
        }

        if (payment.Method != PaymentMethod.Online)
        {
            return Result.Failure<RequestedRefund>(PaymentRefundErrors.NotOnline);
        }

        // DEC-184: serializes with payments, online successes and other refunds of the same enrollment.
        await enrollments.FindLockedAsync(payment.EnrollmentId, cancellationToken);

        // A concurrent request may have committed while this one waited for the lock: read again under it.
        if (Replay(await refunds.FindByIdempotencyKeyAsync(key, cancellationToken), hash) is { } concurrent)
        {
            return concurrent;
        }

        payment = await payments.FindAsync(command.PaymentId, cancellationToken) ?? payment;
        if (await refunds.FindActiveAsync(payment.Id, cancellationToken) is { } active)
        {
            return Result.Failure<RequestedRefund>(
                active.Status == PaymentRefundStatus.Succeeded ? PaymentRefundErrors.AlreadyRefunded : PaymentRefundErrors.InProgress);
        }

        var onlinePayment = payment.OnlinePaymentId is { } onlineId ? await onlinePayments.FindAsync(onlineId, cancellationToken) : null;
        if (onlinePayment is null)
        {
            return Result.Failure<RequestedRefund>(PaymentRefundErrors.NotRefundable);
        }

        // Only the provider that took the money can return it (DEC-418).
        if (providers.Active is not { } provider || !string.Equals(provider.Name, onlinePayment.Provider, StringComparison.Ordinal))
        {
            return Result.Failure<RequestedRefund>(OnlinePaymentErrors.Disabled);
        }

        var requestedBy = UserId.From(currentUser.UserId ?? throw new InvalidOperationException("A refund needs a signed-in user."));
        var requested = PaymentRefund.Request(payment, onlinePayment, command.Reason!, key, hash, requestedBy, clock.GetUtcNow());
        if (requested.IsFailure)
        {
            return Result.Failure<RequestedRefund>(requested.Error);
        }

        refunds.Add(requested.Value);
        return Result.Success(new RequestedRefund(requested.Value.Id, Replayed: false));
    }

    private static Result<RequestedRefund>? Replay(PaymentRefund? earlier, string hash) =>
        earlier is null
            ? null
            : string.Equals(earlier.RequestHash, hash, StringComparison.Ordinal)
                ? Result.Success(new RequestedRefund(earlier.Id, Replayed: true))
                : Result.Failure<RequestedRefund>(IdempotencyErrors.KeyReused);
}

/// <summary>
/// The refund state machine's single entry for settled provider answers (DEC-417, DEC-418). A success marks the payment
/// <see cref="PaymentStatus.Refunded"/> in the same transaction (and is a no-op when repeated); a refusal is final; a
/// request the provider could not settle is marked for review. Under the enrollment lock and the rows' <c>xmin</c>.
/// </summary>
internal sealed class ApplyRefundResultHandler(
    IPaymentRefundRepository refunds,
    IPaymentRepository payments,
    IEnrollmentDirectory enrollments,
    TimeProvider clock) : ICommandHandler<ApplyRefundResultCommand, PaymentRefundStatus>
{
    public async Task<Result<PaymentRefundStatus>> Handle(ApplyRefundResultCommand command, CancellationToken cancellationToken)
    {
        var refund = await refunds.FindAsync(command.Id, cancellationToken);
        if (refund is null)
        {
            return Result.Failure<PaymentRefundStatus>(PaymentRefundErrors.NotFound);
        }

        await enrollments.FindLockedAsync(refund.EnrollmentId, cancellationToken);
        refund = await refunds.GetAsync(command.Id, cancellationToken);
        var now = clock.GetUtcNow();
        switch (command.Resolution)
        {
            case RefundResolution.Succeeded:
                var payment = await payments.GetAsync(refund!.PaymentId, cancellationToken);
                var succeeded = refund.Succeed(payment!, command.ProviderRefundId ?? $"refund-{refund.Id.Value:N}", now);
                if (succeeded.IsFailure)
                {
                    return Result.Failure<PaymentRefundStatus>(succeeded.Error);
                }

                break;

            case RefundResolution.Failed:
                refund!.Fail(command.FailureCode ?? "ProviderDeclined", now);
                break;

            default:
                refund!.MarkForReview();
                break;
        }

        return Result.Success(refund.Status);
    }
}

/// <summary>
/// Refunding an online payment (P3-13, DEC-418, DEC-419). The request is committed first. Every provider call
/// is then claimed with a conditional update committed on its own (so a crash or a lost answer still leaves the attempt
/// counted, and two callers never call the provider for the same request at once), the provider is called outside any
/// transaction or lock, and a settled answer is stored. A refusal of a request that was tried before is not trusted:
/// the money may already be back (its first answer was lost), so the provider's payment detail decides.
/// </summary>
public sealed partial class PaymentRefunds(
    ICommandHandler<RequestRefundCommand, RequestedRefund> request,
    ICommandHandler<ApplyRefundResultCommand, PaymentRefundStatus> apply,
    IPaymentRefundRepository refunds,
    IPaymentRepository payments,
    IPaymentProviders providers,
    TimeProvider clock,
    ILogger<PaymentRefunds> logger)
{
    /// <summary>A School Admin's provider check (DEC-418) waits this long after the last call, so it never races one.</summary>
    public static readonly TimeSpan CheckQuietPeriod = TimeSpan.FromMinutes(1);

    /// <summary>The failure code of a request a School Admin checked and the provider shows as not refunded.</summary>
    public const string NotRefundedAtProvider = "NotRefundedAtProvider";

    /// <summary>After this many provider calls the reconciliation job stops and marks the request for review (DEC-418).</summary>
    public const int MaxAutomaticAttempts = 3;

    public async Task<Result<RefundOutcome>> RefundAsync(Guid paymentId, string? reason, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var command = new RequestRefundCommand(PaymentId.From(paymentId), reason, idempotencyKey);
        var requested = await request.Handle(command, cancellationToken);
        if (requested.IsFailure && requested.Error.Code == IdempotencyErrors.InProgress.Code)
        {
            // A concurrent request with the same key won the insert and has committed: this is now a replay.
            requested = await request.Handle(command, cancellationToken);
        }

        if (requested.IsFailure)
        {
            return Result.Failure<RefundOutcome>(requested.Error);
        }

        var refund = await refunds.FindAsync(requested.Value.Id, cancellationToken);
        if (refund is null)
        {
            return Result.Failure<RefundOutcome>(PaymentRefundErrors.NotFound);
        }

        var status = requested.Value.Replayed ? refund.Status : await SendAsync(refund, lastAttemptBefore: null, CancellationToken.None);
        return Result.Success(new RefundOutcome(refund.Id, refund.PaymentId, status, requested.Value.Replayed));
    }

    /// <summary>
    /// "Sağlayıcıda kontrol et" (DEC-418): a School Admin settles the payment's pending refund from the provider's payment
    /// detail. The money is back: refunded. It is not: the request fails (a new request is the retry). The provider cannot
    /// answer: it stays pending.
    /// </summary>
    public async Task<Result<RefundOutcome>> CheckAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var id = PaymentId.From(paymentId);
        if (await payments.FindAsync(id, cancellationToken) is null)
        {
            return Result.Failure<RefundOutcome>(PaymentErrors.NotFound);
        }

        if (await refunds.FindActiveAsync(id, cancellationToken) is not { Status: PaymentRefundStatus.Pending } refund)
        {
            return Result.Failure<RefundOutcome>(PaymentRefundErrors.NotPending);
        }

        if (ActiveProviderOf(refund) is not { } provider)
        {
            return Result.Failure<RefundOutcome>(OnlinePaymentErrors.Disabled);
        }

        var now = clock.GetUtcNow();
        if (!await refunds.TryClaimAttemptAsync(refund.Id, refund.Attempts, now, now - CheckQuietPeriod, CancellationToken.None))
        {
            return Result.Failure<RefundOutcome>(PaymentRefundErrors.InProgress);
        }

        // Only an attempt older than the reconciliation threshold can be declared failed: a younger one may still be
        // finishing at the provider. Until then a "not refunded" detail leaves the request pending (202).
        var settled = (refund.LastAttemptAt ?? refund.RequestedAt) <= now - providers.ReconcileAfter;
        var status = await VerifyAsync(refund, provider, settled ? RefundResolution.Failed : null, NotRefundedAtProvider);
        return Result.Success(new RefundOutcome(refund.Id, refund.PaymentId, status, Replayed: false));
    }

    /// <summary>
    /// Asks the provider to refund a pending request and stores a settled answer; returns where it stands. Nothing is sent
    /// when another caller claimed the attempt first (or, with <paramref name="lastAttemptBefore"/>, when the last call is
    /// too recent).
    /// </summary>
    internal async Task<PaymentRefundStatus> SendAsync(PaymentRefund refund, DateTimeOffset? lastAttemptBefore, CancellationToken cancellationToken)
    {
        if (refund.Status != PaymentRefundStatus.Pending || refund.NeedsReview)
        {
            return refund.Status;
        }

        if (ActiveProviderOf(refund) is not { } provider)
        {
            LogProviderMissing(logger, refund.Id.Value, refund.Provider);
            return PaymentRefundStatus.Pending;
        }

        // DEC-418: the automatic retries stop after a few attempts (unknown outcomes and inconclusive checks both count);
        // a person settles the rest instead of the job sending forever.
        if (lastAttemptBefore is not null && refund.Attempts >= MaxAutomaticAttempts)
        {
            LogNeedsReview(logger, refund.Id.Value, provider.Name, "MaxAttempts");
            return await ApplyAsync(refund, provider, RefundResolution.NeedsReview, null, null);
        }

        if (!await refunds.TryClaimAttemptAsync(refund.Id, refund.Attempts, clock.GetUtcNow(), lastAttemptBefore, CancellationToken.None))
        {
            return (await refunds.FindAsync(refund.Id, CancellationToken.None))?.Status ?? PaymentRefundStatus.Pending;
        }

        var retry = refund.Attempts > 0;
        ProviderRefundResult result;
        try
        {
            result = await provider.RefundAsync(new RefundRequest(refund.Id.Value, refund.ProviderPaymentId, refund.Amount), cancellationToken);
        }
        catch (PaymentProviderException exception)
        {
            LogRefundUnknown(logger, refund.Id.Value, provider.Name, exception.Message);
            return PaymentRefundStatus.Pending;
        }

        return result.Outcome switch
        {
            ProviderRefundOutcome.Refunded => await ApplyAsync(refund, provider, RefundResolution.Succeeded, result.ProviderRefundId, null),
            ProviderRefundOutcome.Declined when !retry => await ApplyAsync(refund, provider, RefundResolution.Failed, null, result.FailureCode),
            // A refusal that may mean "already refunded", or any refusal of a retry: the first answer may have been lost.
            _ => await VerifyAsync(refund, provider, retry ? RefundResolution.NeedsReview : RefundResolution.Failed, result.FailureCode),
        };
    }

    private async Task<PaymentRefundStatus> VerifyAsync(PaymentRefund refund, IPaymentProvider provider, RefundResolution? whenNotRefunded, string? failureCode)
    {
        ProviderRefundCheck check;
        try
        {
            check = await provider.CheckRefundAsync(new RefundRequest(refund.Id.Value, refund.ProviderPaymentId, refund.Amount), CancellationToken.None);
        }
        catch (PaymentProviderException exception)
        {
            LogCheckFailed(logger, refund.Id.Value, provider.Name, exception.Message);
            return PaymentRefundStatus.Pending;
        }

        if (check.Refunded)
        {
            return await ApplyAsync(refund, provider, RefundResolution.Succeeded, check.ProviderRefundId, null);
        }

        if (whenNotRefunded is not { } resolution)
        {
            return PaymentRefundStatus.Pending;
        }

        if (resolution == RefundResolution.NeedsReview)
        {
            LogNeedsReview(logger, refund.Id.Value, provider.Name, failureCode);
        }

        return await ApplyAsync(refund, provider, resolution, null, failureCode ?? NotRefundedAtProvider);
    }

    private async Task<PaymentRefundStatus> ApplyAsync(
        PaymentRefund refund,
        IPaymentProvider provider,
        RefundResolution resolution,
        string? providerRefundId,
        string? failureCode)
    {
        var applied = await apply.Handle(new ApplyRefundResultCommand(refund.Id, resolution, providerRefundId, failureCode), CancellationToken.None);
        if (applied.IsFailure)
        {
            // A concurrent answer (xmin) or a final state won; the stored state is the truth.
            LogApplyFailed(logger, refund.Id.Value, applied.Error.Code);
            return (await refunds.FindAsync(refund.Id, CancellationToken.None))?.Status ?? PaymentRefundStatus.Pending;
        }

        LogRefundResult(logger, refund.Id.Value, provider.Name, resolution, failureCode);
        return applied.Value;
    }

    private IPaymentProvider? ActiveProviderOf(PaymentRefund refund) =>
        providers.Active is { } provider && string.Equals(provider.Name, refund.Provider, StringComparison.Ordinal) ? provider : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund {RefundId} waits: its provider {Provider} is not configured")]
    private static partial void LogProviderMissing(ILogger logger, Guid refundId, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund {RefundId} at {Provider} has an unknown outcome ({ProviderError}); it stays pending")]
    private static partial void LogRefundUnknown(ILogger logger, Guid refundId, string provider, string providerError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking refund {RefundId} at {Provider} failed with {ProviderError}; it stays pending")]
    private static partial void LogCheckFailed(ILogger logger, Guid refundId, string provider, string providerError);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refund {RefundId} needs review: {Provider} refused a retry ({FailureCode}) and does not show the money returned")]
    private static partial void LogNeedsReview(ILogger logger, Guid refundId, string provider, string? failureCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the provider's answer for refund {RefundId} failed with {Code}")]
    private static partial void LogApplyFailed(ILogger logger, Guid refundId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} at {Provider}: {Resolution} {FailureCode}")]
    private static partial void LogRefundResult(ILogger logger, Guid refundId, string provider, RefundResolution resolution, string? failureCode);
}

/// <summary>
/// The safety net for refunds whose outcome stayed unknown (DEC-418, DEC-419): in the current school's scope, pending
/// requests (not marked for review) whose last provider call is older than a threshold are sent again with the same
/// refund id. Each send is claimed first, so overlapping runs never call the provider twice for one request.
/// </summary>
public sealed class PaymentRefundReconciliation(IPaymentRefundRepository refunds, PaymentRefunds flow, IPaymentProviders providers, TimeProvider clock)
{
    /// <summary>At most this many requests per school and run; the rest wait for the next run.</summary>
    public const int MaxPerRun = 50;

    /// <returns>How many requests reached a final result.</returns>
    public async Task<int> ReconcileAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        if (providers.Active is null)
        {
            return 0;
        }

        var threshold = clock.GetUtcNow() - olderThan;
        var pending = await refunds.ListPendingSinceBeforeAsync(threshold, MaxPerRun, cancellationToken);
        var settled = 0;
        foreach (var refund in pending)
        {
            settled += await flow.SendAsync(refund, threshold, cancellationToken) == PaymentRefundStatus.Pending ? 0 : 1;
        }

        return settled;
    }
}

/// <summary>Adds the latest refund request of each online payment.</summary>
internal sealed class RefundViews(IPaymentRefundRepository refunds)
{
    public async Task<IReadOnlyDictionary<PaymentId, PaymentRefund>> LatestAsync(IEnumerable<PaymentId> onlinePaymentIds, CancellationToken cancellationToken)
    {
        var ids = onlinePaymentIds.Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<PaymentId, PaymentRefund>() : await refunds.LatestOfPaymentsAsync(ids, cancellationToken);
    }

    public static PaymentRefundDto ToDto(PaymentRefund refund) => new(
        refund.Id.Value,
        refund.Status,
        refund.Reason,
        refund.RequestedBy.Value,
        refund.RequestedAt,
        refund.CompletedAt,
        refund.FailureCode,
        refund.Attempts,
        refund.NeedsReview,
        refund.ResolvedBy?.Value,
        refund.ResolvedAt,
        refund.ResolutionNote);
}
