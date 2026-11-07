using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct OnlinePaymentId(Guid Value) : IEntityId<OnlinePaymentId>
{
    public static OnlinePaymentId From(Guid value) => new(value);
}

/// <summary>
/// Where an online payment is (DEC-342): <see cref="Created"/> until the provider's checkout is opened,
/// <see cref="Pending"/> while the payer is on the provider's page, then <see cref="Succeeded"/> (absorbing),
/// <see cref="Failed"/> or <see cref="Expired"/>.
/// </summary>
public enum OnlinePaymentStatus
{
    Created,
    Pending,
    Succeeded,
    Failed,
    Expired,
}

/// <summary>A provider's confirmed charge, as the provider's own status query reports it (never a webhook's claim).</summary>
public sealed record ProviderCharge(string ProviderPaymentId, Money Amount, DateTimeOffset ConfirmedAt);

public sealed record OnlinePaymentSucceeded(SchoolId TenantId, OnlinePaymentId OnlinePaymentId, PaymentId PaymentId) : IDomainEvent;

/// <summary>
/// One attempt to pay one due (an enrollment's period) through a payment provider's hosted checkout (P2-F1).
/// The amount is fixed when the attempt starts (the remaining due then). The first confirmed success records
/// exactly one <see cref="Payment"/> with <see cref="PaymentMethod.Online"/>; every later success report is a
/// no-op, so a repeated or concurrent webhook can never credit the due twice (DEC-342). The row's <c>xmin</c>
/// and the unique <c>online_payment_id</c> of payments back this up in the database.
/// </summary>
public sealed class OnlinePayment : AggregateRoot<OnlinePaymentId>, ITenantScoped
{
    public const int ProviderMaxLength = 32;
    public const int IdempotencyKeyMaxLength = 100;
    public const int RequestHashLength = 64;
    public const int ProviderReferenceMaxLength = 128;
    public const int CheckoutUrlMaxLength = 2048;
    public const int FailureCodeMaxLength = 64;

    private OnlinePayment(
        OnlinePaymentId id,
        SchoolId tenantId,
        PaidEnrollment enrollment,
        YearMonth period,
        Money amount,
        string provider,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset startedAt)
        : base(id)
    {
        TenantId = tenantId;
        EnrollmentId = enrollment.Id;
        StudentId = enrollment.StudentId;
        GroupId = enrollment.GroupId;
        EnrollmentStartDate = enrollment.StartDate;
        Period = period;
        Amount = amount;
        Provider = provider;
        IdempotencyKey = idempotencyKey;
        RequestHash = requestHash;
        StartedAt = startedAt;
        Status = OnlinePaymentStatus.Created;
    }

    // For EF Core materialization only.
    private OnlinePayment()
        : base(default)
    {
        Period = null!;
        Amount = null!;
        Provider = null!;
        IdempotencyKey = null!;
        RequestHash = null!;
    }

    public SchoolId TenantId { get; private set; }

    public EnrollmentId EnrollmentId { get; private set; }

    public StudentId StudentId { get; private set; }

    public GroupId GroupId { get; private set; }

    /// <summary>Kept so the recorded payment can be built without asking the Enrollments module again.</summary>
    public DateOnly EnrollmentStartDate { get; private set; }

    public YearMonth Period { get; private set; }

    public Money Amount { get; private set; }

    /// <summary>The provider that opened the checkout (<c>Fake</c> or <c>Iyzico</c>).</summary>
    public string Provider { get; private set; }

    /// <summary>The client's <c>Idempotency-Key</c>, unique per school (DEC-341).</summary>
    public string IdempotencyKey { get; private set; }

    /// <summary>SHA-256 (hex) of the canonical request; a reused key with another request is refused.</summary>
    public string RequestHash { get; private set; }

    public OnlinePaymentStatus Status { get; private set; }

    /// <summary>The provider's checkout token; the webhook and callback find the payment (and its school) by it.</summary>
    public string? ProviderToken { get; private set; }

    public string? CheckoutUrl { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CheckoutExpiresAt { get; private set; }

    /// <summary>The provider's id of the charge; unique per provider.</summary>
    public string? ProviderPaymentId { get; private set; }

    public string? FailureCode { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The payment recorded on success.</summary>
    public PaymentId? PaymentId { get; private set; }

    public bool IsOpen => Status is OnlinePaymentStatus.Created or OnlinePaymentStatus.Pending;

    /// <summary>Starts paying <paramref name="remaining"/> of a due; nothing is charged yet.</summary>
    public static Result<OnlinePayment> Start(
        SchoolId tenantId,
        PaidEnrollment enrollment,
        YearMonth period,
        Money remaining,
        string provider,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        if (remaining.Amount <= 0)
        {
            return Result.Failure<OnlinePayment>(OnlinePaymentErrors.NothingDue);
        }

        if (!DueCalculator.WasActiveIn(enrollment.StartDate, enrollment.EndDate, period))
        {
            return Result.Failure<OnlinePayment>(PaymentErrors.EnrollmentNotActiveInPeriod);
        }

        return Result.Success(new OnlinePayment(
            EntityId.New<OnlinePaymentId>(), tenantId, enrollment, period, remaining, provider, idempotencyKey, requestHash, now));
    }

    /// <summary>The provider opened its hosted checkout: the payer can now pay at <paramref name="checkoutUrl"/>.</summary>
    public Result OpenCheckout(string providerToken, string checkoutUrl, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutUrl);

        if (Status != OnlinePaymentStatus.Created)
        {
            return Result.Failure(OnlinePaymentErrors.NotOpen);
        }

        ProviderToken = providerToken;
        CheckoutUrl = checkoutUrl;
        CheckoutExpiresAt = expiresAt;
        Status = OnlinePaymentStatus.Pending;
        return Result.Success();
    }

    /// <summary>
    /// The provider confirmed the charge. The first confirmation records the payment and returns it; a repeated one
    /// (the same provider payment) returns <c>null</c> and changes nothing. A success is accepted even after a
    /// failure or expiry, because the money was taken; only the amount and currency must match.
    /// </summary>
    public Result<Payment?> Succeed(ProviderCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        ArgumentException.ThrowIfNullOrWhiteSpace(charge.ProviderPaymentId);

        if (Status == OnlinePaymentStatus.Succeeded)
        {
            return string.Equals(ProviderPaymentId, charge.ProviderPaymentId, StringComparison.Ordinal)
                ? Result.Success<Payment?>(null)
                : Result.Failure<Payment?>(OnlinePaymentErrors.ProviderMismatch);
        }

        if (charge.Amount != Amount)
        {
            return Result.Failure<Payment?>(OnlinePaymentErrors.ProviderMismatch);
        }

        var payment = Payment.RecordOnline(
            TenantId,
            new PaidEnrollment(EnrollmentId, StudentId, GroupId, EnrollmentStartDate, null),
            Period,
            Amount,
            DateOnly.FromDateTime(charge.ConfirmedAt.UtcDateTime),
            Id);

        Status = OnlinePaymentStatus.Succeeded;
        ProviderPaymentId = charge.ProviderPaymentId;
        FailureCode = null;
        CompletedAt = charge.ConfirmedAt;
        PaymentId = payment.Id;
        Raise(new OnlinePaymentSucceeded(TenantId, Id, payment.Id));
        return Result.Success<Payment?>(payment);
    }

    /// <summary>The provider reported a failure (or the checkout could not be opened). A success is never undone.</summary>
    /// <returns><c>true</c> when the status changed.</returns>
    public bool Fail(string failureCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (!IsOpen)
        {
            return false;
        }

        Status = OnlinePaymentStatus.Failed;
        FailureCode = failureCode.Length > FailureCodeMaxLength ? failureCode[..FailureCodeMaxLength] : failureCode;
        CompletedAt = now;
        return true;
    }

    /// <summary>An open attempt whose checkout lifetime has passed no longer blocks a new attempt for the same due.</summary>
    public bool HasExpired(DateTimeOffset now, TimeSpan createdLifetime) =>
        IsOpen && (CheckoutExpiresAt is { } expires ? expires <= now : StartedAt + createdLifetime <= now);
}

public static class OnlinePaymentErrors
{
    public static readonly Error NotFound = Error.NotFound("OnlinePayment.NotFound", "Online payment not found.");
    public static readonly Error NothingDue = Error.BusinessRule("OnlinePayment.NothingDue", "Nothing is due for the period.");
    public static readonly Error PeriodInFuture = Error.BusinessRule("OnlinePayment.PeriodInFuture", "A future period cannot be paid yet.");
    public static readonly Error CheckoutInProgress = Error.Conflict("OnlinePayment.CheckoutInProgress", "Another online payment of the due is in progress.");
    public static readonly Error NotOpen = Error.BusinessRule("OnlinePayment.NotOpen", "The online payment is no longer open.");
    public static readonly Error ProviderUnavailable = Error.BusinessRule("OnlinePayment.ProviderUnavailable", "The payment provider could not be reached.");
    public static readonly Error ProviderMismatch = Error.BusinessRule("OnlinePayment.ProviderMismatch", "The provider's report does not match the payment.");
    public static readonly Error Disabled = Error.BusinessRule("OnlinePayment.Disabled", "Online payment is not configured.");
    public static readonly Error InvalidSignature = Error.Unauthorized("OnlinePayment.InvalidSignature", "The notification signature is not valid.");
}

public static class IdempotencyErrors
{
    public static readonly Error KeyRequired = Error.Validation("Idempotency.KeyRequired", "An Idempotency-Key header is required.");
    public static readonly Error KeyInvalid = Error.Validation("Idempotency.KeyInvalid", "The Idempotency-Key header is not valid.");
    public static readonly Error KeyReused = Error.BusinessRule("Idempotency.KeyReused", "The Idempotency-Key was used for a different request.");
    public static readonly Error InProgress = Error.Conflict("Idempotency.InProgress", "A request with the same Idempotency-Key is in progress.");
}
