using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>Read contract of the Billing module for a student's erasure (P4-06, DEC-546).</summary>
public interface IStudentPaymentActivity
{
    /// <summary>
    /// Whether money of the student is still in flight at the provider: an online payment attempt not yet finished
    /// (<c>Created</c>, <c>Pending</c>) or a refund not yet settled (<c>Pending</c>, including one that needs review).
    /// A provider callback for either would create or change a payment of the student.
    /// </summary>
    Task<bool> HasOpenActivityAsync(StudentId studentId, CancellationToken cancellationToken);
}
