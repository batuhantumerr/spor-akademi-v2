using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct PaymentRefundId(Guid Value) : IEntityId<PaymentRefundId>
{
    public static PaymentRefundId From(Guid value) => new(value);
}

/// <summary>
/// Where a refund request is (P3-13, DEC-417): <see cref="Pending"/> from the request until the provider gives a final
/// answer (also while the answer is unknown, e.g. a timeout), then <see cref="Succeeded"/> (absorbing; the payment is
/// <see cref="PaymentStatus.Refunded"/>) or <see cref="Failed"/> (the provider refused; final, a new request may follow).
/// </summary>
public enum PaymentRefundStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>A refund was confirmed by the provider and committed: the payment no longer counts toward its due.</summary>
public sealed record PaymentRefunded(SchoolId TenantId, PaymentRefundId RefundId, PaymentId PaymentId, StudentId StudentId, GroupId GroupId) : IDomainEvent;

/// <summary>
/// One request to return the full amount of an online payment through the provider that took it (P3-13, DEC-416). The
/// request is committed first, the provider is called outside any transaction and its answer is stored by
/// <see cref="Succeed"/> or <see cref="Fail"/>. At most one request per payment is <see cref="PaymentRefundStatus.Pending"/>
/// or <see cref="PaymentRefundStatus.Succeeded"/> (a partial unique index), so a payment is never refunded twice; a
/// failed request stays as history and a new one (with a new key) is the retry.
/// </summary>
public sealed class PaymentRefund : AggregateRoot<PaymentRefundId>, ITenantScoped
{
    public const int ReasonMaxLength = 500;
    public const int FailureCodeMaxLength = OnlinePayment.FailureCodeMaxLength;

    private PaymentRefund(
        PaymentRefundId id,
        Payment payment,
        OnlinePayment onlinePayment,
        string reason,
        string idempotencyKey,
        string requestHash,
        UserId requestedBy,
        DateTimeOffset requestedAt)
        : base(id)
    {
        TenantId = payment.TenantId;
        PaymentId = payment.Id;
        OnlinePaymentId = onlinePayment.Id;
        EnrollmentId = payment.EnrollmentId;
        StudentId = payment.StudentId;
        Amount = payment.Amount;
        Provider = onlinePayment.Provider;
        ProviderPaymentId = onlinePayment.ProviderPaymentId!;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        RequestedBy = requestedBy;
        RequestedAt = requestedAt;
        Status = PaymentRefundStatus.Pending;
    }

    // For EF Core materialization only.
    private PaymentRefund()
        : base(default)
    {
        Amount = null!;
        Provider = null!;
        ProviderPaymentId = null!;
        Reason = null!;
        IdempotencyKey = null!;
        RequestHash = null!;
    }

    public SchoolId TenantId { get; private set; }

    public PaymentId PaymentId { get; private set; }

    public OnlinePaymentId OnlinePaymentId { get; private set; }

    /// <summary>The payment's enrollment; its row lock serializes the refund with the other changes of the due (DEC-184).</summary>
    public EnrollmentId EnrollmentId { get; private set; }

    public StudentId StudentId { get; private set; }

    /// <summary>Always the payment's full amount (no partial refunds, DEC-416).</summary>
    public Money Amount { get; private set; }

    /// <summary>The provider that took the money; only it can return it.</summary>
    public string Provider { get; private set; }

    /// <summary>The provider's id of the charge being refunded.</summary>
    public string ProviderPaymentId { get; private set; }

    /// <summary>Why the school returns the money; for the school only (never sent to the parent or the provider).</summary>
    public string Reason { get; private set; }

    /// <summary>The client's <c>Idempotency-Key</c>, unique per school (DEC-419).</summary>
    public string IdempotencyKey { get; private set; }

    /// <summary>SHA-256 (hex) of the canonical request; a reused key with another request is refused.</summary>
    public string RequestHash { get; private set; }

    public UserId RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public PaymentRefundStatus Status { get; private set; }

    /// <summary>How many times the provider was asked (the request and the reconciliation job's retries).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>The provider's reference of the refund, once it succeeded.</summary>
    public string? ProviderRefundId { get; private set; }

    /// <summary>The provider's error code when it refused (never its free-text message).</summary>
    public string? FailureCode { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// A pending request the provider could not settle on its own (DEC-418): a retry was refused and the provider's
    /// payment detail does not show the money returned. The reconciliation job leaves it alone; a School Admin checks it
    /// at the provider ("Sağlayıcıda kontrol et").
    /// </summary>
    public bool NeedsReview { get; private set; }

    /// <summary>
    /// Requests the full refund of an online payment. The payment must still count toward its due
    /// (<see cref="PaymentStatus.Recorded"/>) and be the one the succeeded online payment recorded.
    /// </summary>
    public static Result<PaymentRefund> Request(
        Payment payment,
        OnlinePayment onlinePayment,
        string reason,
        string idempotencyKey,
        string requestHash,
        UserId requestedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(onlinePayment);
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        if (payment.Method != PaymentMethod.Online)
        {
            return Result.Failure<PaymentRefund>(PaymentRefundErrors.NotOnline);
        }

        if (payment.Status == PaymentStatus.Refunded)
        {
            return Result.Failure<PaymentRefund>(PaymentRefundErrors.AlreadyRefunded);
        }

        if (payment.Status != PaymentStatus.Recorded
            || onlinePayment.Status != OnlinePaymentStatus.Succeeded
            || onlinePayment.PaymentId != payment.Id
            || onlinePayment.TenantId != payment.TenantId
            || string.IsNullOrEmpty(onlinePayment.ProviderPaymentId))
        {
            return Result.Failure<PaymentRefund>(PaymentRefundErrors.NotRefundable);
        }

        var trimmed = reason.Trim();
        if (trimmed.Length == 0)
        {
            return Result.Failure<PaymentRefund>(PaymentRefundErrors.ReasonRequired);
        }

        if (trimmed.Length > ReasonMaxLength)
        {
            return Result.Failure<PaymentRefund>(CommonErrors.FieldTooLong);
        }

        return Result.Success(new PaymentRefund(
            EntityId.New<PaymentRefundId>(), payment, onlinePayment, trimmed, idempotencyKey, requestHash, requestedBy, now));
    }

    /// <summary>The provider was asked; its answer (if any) is stored next.</summary>
    public void RecordAttempt(DateTimeOffset now)
    {
        if (Status != PaymentRefundStatus.Pending)
        {
            return;
        }

        Attempts++;
        LastAttemptAt = now;
    }

    /// <summary>
    /// The provider returned the money: the payment becomes <see cref="PaymentStatus.Refunded"/> in the same transaction.
    /// A repeated confirmation changes nothing (<c>false</c>); a failed request cannot succeed later (its answer was final).
    /// </summary>
    /// <returns><c>true</c> when the state changed.</returns>
    public Result<bool> Succeed(Payment payment, string providerRefundId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRefundId);

        if (payment.Id != PaymentId)
        {
            throw new InvalidOperationException("The refund belongs to another payment.");
        }

        if (Status == PaymentRefundStatus.Succeeded)
        {
            return Result.Success(false);
        }

        if (Status == PaymentRefundStatus.Failed)
        {
            return Result.Failure<bool>(PaymentRefundErrors.NotPending);
        }

        payment.MarkRefunded();
        Status = PaymentRefundStatus.Succeeded;
        NeedsReview = false;
        ProviderRefundId = providerRefundId.Length > OnlinePayment.ProviderReferenceMaxLength
            ? providerRefundId[..OnlinePayment.ProviderReferenceMaxLength]
            : providerRefundId;
        CompletedAt = now;
        Raise(new PaymentRefunded(TenantId, Id, PaymentId, payment.StudentId, payment.GroupId));
        return Result.Success(true);
    }

    /// <summary>A retried refund was refused and the provider does not show the money returned: a person must look (DEC-418).</summary>
    /// <returns><c>true</c> when the state changed.</returns>
    public bool MarkForReview()
    {
        if (Status != PaymentRefundStatus.Pending || NeedsReview)
        {
            return false;
        }

        NeedsReview = true;
        return true;
    }

    /// <summary>The provider refused the refund (a definite answer, not an outage). A success is never undone.</summary>
    /// <returns><c>true</c> when the state changed.</returns>
    public bool Fail(string failureCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (Status != PaymentRefundStatus.Pending)
        {
            return false;
        }

        Status = PaymentRefundStatus.Failed;
        NeedsReview = false;
        FailureCode = failureCode.Length > FailureCodeMaxLength ? failureCode[..FailureCodeMaxLength] : failureCode;
        CompletedAt = now;
        return true;
    }
}

public static class PaymentRefundErrors
{
    public static readonly Error NotFound = Error.NotFound("Refund.NotFound", "Refund not found.");
    public static readonly Error ReasonRequired = Error.Validation("Refund.ReasonRequired", "A refund reason is required.");
    public static readonly Error NotOnline = Error.BusinessRule("Refund.NotOnline", "Only an online payment can be refunded; cancel a manual entry instead.");
    public static readonly Error AlreadyRefunded = Error.BusinessRule("Refund.AlreadyRefunded", "The payment is already refunded.");
    public static readonly Error NotRefundable = Error.BusinessRule("Refund.NotRefundable", "The payment cannot be refunded.");
    public static readonly Error InProgress = Error.Conflict("Refund.InProgress", "A refund of the payment is in progress.");
    public static readonly Error NotPending = Error.BusinessRule("Refund.NotPending", "The refund already has a final result.");
    public static readonly Error ProviderDeclined = Error.BusinessRule("Refund.ProviderDeclined", "The payment provider declined the refund.");
}
