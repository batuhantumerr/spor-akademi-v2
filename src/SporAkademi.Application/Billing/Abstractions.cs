using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>
/// A payment for the School Admin (appendix D §2.10); cancelled ones stay visible with who, when and why. An online
/// payment carries its online attempt and its latest refund request (P3-13), if any; a recorded online payment of a due
/// whose recorded payments exceed what it owes carries the excess (<c>OverpaidBy</c>, live, DEC-420).
/// </summary>
public sealed record PaymentDto(
    Guid Id,
    Guid EnrollmentId,
    Guid StudentId,
    string StudentFirstName,
    string StudentLastName,
    Guid GroupId,
    string GroupName,
    string Period,
    MoneyDto Amount,
    PaymentMethod Method,
    DateOnly PaidOn,
    PaymentStatus Status,
    string? CancelReason,
    Guid? CancelledBy,
    DateTimeOffset? CancelledAt,
    Guid? OnlinePaymentId,
    PaymentRefundDto? Refund,
    MoneyDto? OverpaidBy);

/// <summary>
/// One unpaid period of an enrollment (US-24): what is owed (<see cref="Fee"/>, after the fee rules of P2-F8), what was
/// paid and what remains. <see cref="ListFee"/> is the group's fee and <see cref="Discount"/> the difference, with
/// <see cref="Adjustment"/> and <see cref="DiscountPercent"/> saying why (e.g. a 10 % sibling discount), so the amount
/// can be explained (DEC-373).
/// </summary>
public sealed record OutstandingDueDto(
    Guid EnrollmentId,
    Guid StudentId,
    string StudentFirstName,
    string StudentLastName,
    Guid GroupId,
    string GroupName,
    string Period,
    MoneyDto Fee,
    MoneyDto Paid,
    MoneyDto Remaining,
    MoneyDto ListFee,
    MoneyDto Discount,
    ChargeAdjustment Adjustment,
    int DiscountPercent);

/// <param name="Statuses">When set, only payments in one of these statuses (in addition to <paramref name="Status"/>).</param>
public sealed record PaymentFilter(StudentId? StudentId, GroupId? GroupId, YearMonth? Period, PaymentStatus? Status, IReadOnlyCollection<PaymentStatus>? Statuses = null);

/// <summary>Payments of the current school (tenant filter applies). There is deliberately no update or delete.</summary>
public interface IPaymentRepository
{
    /// <summary>Tracked, for cancelling.</summary>
    Task<Payment?> GetAsync(PaymentId id, CancellationToken cancellationToken);

    void Add(Payment payment);

    Task<Payment?> FindAsync(PaymentId id, CancellationToken cancellationToken);

    /// <summary>Newest payment date first.</summary>
    Task<PagedResult<Payment>> ListAsync(PaymentFilter filter, PageRequest page, CancellationToken cancellationToken);

    /// <summary>
    /// Sum of <see cref="PaymentStatus.Recorded"/> payments per enrollment and period (cancelled and refunded payments never
    /// count, DEC-420), for periods from
    /// <paramref name="firstPeriod"/> to <paramref name="lastPeriod"/> (inclusive), optionally of one group. Summed in the
    /// database as plain decimals, so no total can overflow <see cref="Money"/>.
    /// </summary>
    Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> RecordedTotalsAsync(
        GroupId? groupId,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="RecordedTotalsAsync"/>, but only for the given enrollments (one family's dues, DEC-388), so the
    /// rest of the school's payments are never read.
    /// </summary>
    Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), decimal>> RecordedTotalsOfEnrollmentsAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sum of <see cref="PaymentStatus.Recorded"/> payments per month of the payment date (cash basis) between
    /// <paramref name="firstDay"/> and <paramref name="lastDay"/>, inclusive; months without payments are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<YearMonth, decimal>> CollectedByPaymentMonthAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>Sum of the <see cref="PaymentStatus.Recorded"/> payments of one enrollment and period.</summary>
    Task<decimal> RecordedTotalAsync(EnrollmentId enrollmentId, YearMonth period, CancellationToken cancellationToken);
}
