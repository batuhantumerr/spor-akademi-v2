using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>A payment for the School Admin (appendix D §2.10); cancelled ones stay visible with who, when and why.</summary>
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
    DateTimeOffset? CancelledAt);

/// <summary>One unpaid period of an enrollment (US-24): the group's fee, what was paid and what remains.</summary>
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
    MoneyDto Remaining);

public sealed record PaymentFilter(StudentId? StudentId, GroupId? GroupId, YearMonth? Period, PaymentStatus? Status);

/// <summary>Payments of the current school (tenant filter applies). There is deliberately no update or delete.</summary>
public interface IPaymentRepository
{
    /// <summary>Tracked, for cancelling.</summary>
    Task<Payment?> GetAsync(PaymentId id, CancellationToken cancellationToken);

    void Add(Payment payment);

    Task<Payment?> FindAsync(PaymentId id, CancellationToken cancellationToken);

    /// <summary>Newest payment date first.</summary>
    Task<PagedResult<Payment>> ListAsync(PaymentFilter filter, PageRequest page, CancellationToken cancellationToken);

    /// <summary>Sum of <see cref="PaymentStatus.Recorded"/> payments per enrollment and period.</summary>
    Task<IReadOnlyDictionary<(EnrollmentId Enrollment, YearMonth Period), Money>> RecordedTotalsAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        CancellationToken cancellationToken);
}
