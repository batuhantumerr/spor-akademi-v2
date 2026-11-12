using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct PaymentId(Guid Value) : IEntityId<PaymentId>
{
    public static PaymentId From(Guid value) => new(value);
}

public enum PaymentMethod
{
    Cash,
    BankTransfer,

    /// <summary>Paid through a payment provider (P2-F1); only recorded by <see cref="OnlinePayment"/>, never by hand.</summary>
    Online,
}

public enum PaymentStatus
{
    Recorded,
    Cancelled,
}

/// <summary>A payment was recorded, by hand or online (P3-08: the student's parents are told after commit).</summary>
public sealed record PaymentRecorded(SchoolId TenantId, PaymentId PaymentId, StudentId StudentId, GroupId GroupId) : IDomainEvent;

/// <summary>The enrollment a payment is for, as the Enrollments module reports it.</summary>
public sealed record PaidEnrollment(EnrollmentId Id, StudentId StudentId, GroupId GroupId, DateOnly StartDate, DateOnly? EndDate);

/// <summary>
/// A manually recorded fee payment (appendix B 4.10, US-23). Never edited or deleted, so history stays
/// auditable: a wrong entry is cancelled with a reason and recorded again. Only <see cref="PaymentStatus.Recorded"/>
/// payments reduce the due, which is computed, never stored (DEC-021, <see cref="DueCalculator"/>).
/// </summary>
public sealed class Payment : AggregateRoot<PaymentId>, ITenantScoped
{
    public const int CancelReasonMaxLength = 500;

    private Payment(PaymentId id, SchoolId tenantId, PaidEnrollment enrollment, YearMonth period, Money amount, PaymentMethod method, DateOnly paidOn)
        : base(id)
    {
        TenantId = tenantId;
        EnrollmentId = enrollment.Id;
        StudentId = enrollment.StudentId;
        GroupId = enrollment.GroupId;
        Period = period;
        Amount = amount;
        Method = method;
        PaidOn = paidOn;
        Status = PaymentStatus.Recorded;
    }

    // For EF Core materialization only.
    private Payment()
        : base(default)
    {
        Period = null!;
        Amount = null!;
    }

    public SchoolId TenantId { get; private set; }

    public EnrollmentId EnrollmentId { get; private set; }

    /// <summary>Denormalized from the enrollment for reporting (appendix B 4.10).</summary>
    public StudentId StudentId { get; private set; }

    /// <summary>Denormalized from the enrollment (which never changes group) for the group filter (DEC-182).</summary>
    public GroupId GroupId { get; private set; }

    public YearMonth Period { get; private set; }

    public Money Amount { get; private set; }

    public PaymentMethod Method { get; private set; }

    public DateOnly PaidOn { get; private set; }

    public PaymentStatus Status { get; private set; }

    public string? CancelReason { get; private set; }

    public UserId? CancelledBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>The online payment that recorded this payment; <c>null</c> for cash and bank transfer.</summary>
    public OnlinePaymentId? OnlinePaymentId { get; private set; }

    /// <summary>
    /// Records a payment. The enrollment must have been active at least one day of <paramref name="period"/>
    /// and the group must have a fee for it (<paramref name="feeDefined"/>, looked up by the caller).
    /// </summary>
    public static Result<Payment> Record(
        SchoolId tenantId,
        PaidEnrollment enrollment,
        YearMonth period,
        Money amount,
        PaymentMethod method,
        DateOnly paidOn,
        bool feeDefined)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            return Result.Failure<Payment>(PaymentErrors.InvalidAmount);
        }

        if (!DueCalculator.WasActiveIn(enrollment.StartDate, enrollment.EndDate, period))
        {
            return Result.Failure<Payment>(PaymentErrors.EnrollmentNotActiveInPeriod);
        }

        if (!feeDefined)
        {
            return Result.Failure<Payment>(PaymentErrors.FeeNotDefined);
        }

        if (method == PaymentMethod.Online)
        {
            return Result.Failure<Payment>(CommonErrors.FieldInvalid);
        }

        return Result.Success(Recorded(new Payment(EntityId.New<PaymentId>(), tenantId, enrollment, period, amount, method, paidOn)));
    }

    /// <summary>
    /// Records a charge the provider confirmed (DEC-342). The due's rules were checked when the online payment started;
    /// the money is taken, so it is recorded even if the enrollment changed since.
    /// </summary>
    internal static Payment RecordOnline(SchoolId tenantId, PaidEnrollment enrollment, YearMonth period, Money amount, DateOnly paidOn, OnlinePaymentId onlinePaymentId) =>
        Recorded(new Payment(EntityId.New<PaymentId>(), tenantId, enrollment, period, amount, PaymentMethod.Online, paidOn) { OnlinePaymentId = onlinePaymentId });

    private static Payment Recorded(Payment payment)
    {
        payment.Raise(new PaymentRecorded(payment.TenantId, payment.Id, payment.StudentId, payment.GroupId));
        return payment;
    }

    /// <summary>Cancels a wrong entry with a reason; who and when are kept. The amount and the rest never change.</summary>
    public Result Cancel(string reason, UserId cancelledBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (Status == PaymentStatus.Cancelled)
        {
            return Result.Failure(PaymentErrors.AlreadyCancelled);
        }

        // DEC-346: the money of an online payment was really taken; cancelling it here would reopen the due for a
        // second charge. Refunds go through the provider (not in P2-F1).
        if (Method == PaymentMethod.Online)
        {
            return Result.Failure(PaymentErrors.OnlineNotCancellable);
        }

        var trimmed = reason.Trim();
        if (trimmed.Length == 0)
        {
            return Result.Failure(PaymentErrors.CancelReasonRequired);
        }

        if (trimmed.Length > CancelReasonMaxLength)
        {
            return Result.Failure(CommonErrors.FieldTooLong);
        }

        Status = PaymentStatus.Cancelled;
        CancelReason = trimmed;
        CancelledBy = cancelledBy;
        CancelledAt = now;
        return Result.Success();
    }
}

public static class PaymentErrors
{
    public static readonly Error NotFound = Error.NotFound("Payment.NotFound", "Payment not found.");
    public static readonly Error InvalidAmount = Error.Validation("Payment.InvalidAmount", "The amount must be greater than zero.");
    public static readonly Error EnrollmentNotActiveInPeriod = Error.BusinessRule("Payment.EnrollmentNotActiveInPeriod", "The enrollment was not active in the period.");
    public static readonly Error FeeNotDefined = Error.BusinessRule("Payment.FeeNotDefined", "The group has no fee for the period.");
    public static readonly Error AlreadyCancelled = Error.BusinessRule("Payment.AlreadyCancelled", "The payment is already cancelled.");
    public static readonly Error OnlinePaymentInProgress = Error.Conflict("Payment.OnlinePaymentInProgress", "An online payment of the due is in progress.");
    public static readonly Error OnlineNotCancellable = Error.BusinessRule("Payment.OnlineNotCancellable", "An online payment cannot be cancelled here.");
    public static readonly Error CancelReasonRequired = Error.Validation("Payment.CancelReasonRequired", "A cancel reason is required.");
}
