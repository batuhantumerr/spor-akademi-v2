using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Billing;
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
    int Attempts);

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

    /// <summary>Pending requests whose last provider call (or the request) is before <paramref name="before"/>, oldest first.</summary>
    Task<IReadOnlyList<PaymentRefund>> ListPendingSinceBeforeAsync(DateTimeOffset before, int max, CancellationToken cancellationToken);

    void Add(PaymentRefund refund);
}

/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header (DEC-419).</param>
public sealed record RequestRefundCommand(PaymentId PaymentId, string? Reason, string? IdempotencyKey) : ICommand<RequestedRefund>;

/// <param name="Replayed">An earlier request with the same key and body created it; nothing new was requested.</param>
public sealed record RequestedRefund(PaymentRefundId Id, bool Replayed);

/// <summary>
/// The provider's answer to a refund request: <paramref name="Result"/> is <c>null</c> when the outcome is unknown (the
/// call only counts as an attempt and the request stays pending).
/// </summary>
public sealed record ApplyRefundResultCommand(PaymentRefundId Id, ProviderRefundResult? Result) : ICommand<PaymentRefundStatus>;

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
/// The refund state machine's single entry for provider answers (DEC-417). A success marks the payment
/// <see cref="PaymentStatus.Refunded"/> in the same transaction (and is a no-op when repeated); a refusal is final; an
/// unknown outcome only counts the attempt. Under the enrollment lock and the rows' <c>xmin</c>.
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
        var payment = await payments.GetAsync(refund!.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();
        refund.RecordAttempt(now);

        switch (command.Result)
        {
            case { Succeeded: true, ProviderRefundId: { } providerRefundId }:
                var succeeded = refund.Succeed(payment!, providerRefundId, now);
                if (succeeded.IsFailure)
                {
                    return Result.Failure<PaymentRefundStatus>(succeeded.Error);
                }

                break;

            case { Succeeded: false } declined:
                refund.Fail(declined.FailureCode ?? "ProviderDeclined", now);
                break;
        }

        return Result.Success(refund.Status);
    }
}

/// <summary>
/// Refunding an online payment (P3-13, DEC-418): the request is committed first, then the provider is called outside any
/// database transaction or lock, then its answer is stored. When the outcome is unknown the request stays pending and
/// <see cref="PaymentRefundReconciliation"/> asks again later; a replay of the same key never calls the provider.
/// </summary>
public sealed partial class PaymentRefunds(
    ICommandHandler<RequestRefundCommand, RequestedRefund> request,
    ICommandHandler<ApplyRefundResultCommand, PaymentRefundStatus> apply,
    IPaymentRefundRepository refunds,
    IPaymentProviders providers,
    ILogger<PaymentRefunds> logger)
{
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

        var status = requested.Value.Replayed ? refund.Status : await SendAsync(refund, CancellationToken.None);
        return Result.Success(new RefundOutcome(refund.Id, refund.PaymentId, status, requested.Value.Replayed));
    }

    /// <summary>Asks the provider to refund a pending request and stores a definite answer; returns where it stands.</summary>
    internal async Task<PaymentRefundStatus> SendAsync(PaymentRefund refund, CancellationToken cancellationToken)
    {
        if (refund.Status != PaymentRefundStatus.Pending)
        {
            return refund.Status;
        }

        if (providers.Active is not { } provider || !string.Equals(provider.Name, refund.Provider, StringComparison.Ordinal))
        {
            LogProviderMissing(logger, refund.Id.Value, refund.Provider);
            return PaymentRefundStatus.Pending;
        }

        ProviderRefundResult? result;
        try
        {
            result = await provider.RefundAsync(new RefundRequest(refund.Id.Value, refund.ProviderPaymentId, refund.Amount), cancellationToken);
        }
        catch (PaymentProviderException exception)
        {
            LogRefundUnknown(logger, refund.Id.Value, provider.Name, exception.Message);
            result = null;
        }

        var applied = await apply.Handle(new ApplyRefundResultCommand(refund.Id, result), CancellationToken.None);
        if (applied.IsFailure)
        {
            // A concurrent answer (xmin) or a final state won; the stored state is the truth.
            LogApplyFailed(logger, refund.Id.Value, applied.Error.Code);
            return (await refunds.FindAsync(refund.Id, CancellationToken.None))?.Status ?? PaymentRefundStatus.Pending;
        }

        LogRefundResult(logger, refund.Id.Value, provider.Name, applied.Value, result?.FailureCode);
        return applied.Value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund {RefundId} waits: its provider {Provider} is not configured")]
    private static partial void LogProviderMissing(ILogger logger, Guid refundId, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund {RefundId} at {Provider} has an unknown outcome ({ProviderError}); it stays pending")]
    private static partial void LogRefundUnknown(ILogger logger, Guid refundId, string provider, string providerError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the provider's answer for refund {RefundId} failed with {Code}")]
    private static partial void LogApplyFailed(ILogger logger, Guid refundId, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} at {Provider}: {Status} {FailureCode}")]
    private static partial void LogRefundResult(ILogger logger, Guid refundId, string provider, PaymentRefundStatus status, string? failureCode);
}

/// <summary>
/// The safety net for refunds whose outcome stayed unknown (DEC-418): in the current school's scope, pending requests
/// whose last provider call is older than a threshold are sent again with the same refund id. Run per school by the
/// online payment reconciliation job.
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

        var pending = await refunds.ListPendingSinceBeforeAsync(clock.GetUtcNow() - olderThan, MaxPerRun, cancellationToken);
        var settled = 0;
        foreach (var refund in pending)
        {
            settled += await flow.SendAsync(refund, cancellationToken) == PaymentRefundStatus.Pending ? 0 : 1;
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
        refund.Attempts);
}
