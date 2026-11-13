using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

/// <inheritdoc cref="IStudentPaymentActivity"/>
internal sealed class StudentPaymentActivity(AppDbContext db) : IStudentPaymentActivity
{
    public async Task<bool> HasOpenActivityAsync(StudentId studentId, CancellationToken cancellationToken) =>
        await db.Set<OnlinePayment>().AnyAsync(
            p => p.StudentId == studentId && (p.Status == OnlinePaymentStatus.Created || p.Status == OnlinePaymentStatus.Pending),
            cancellationToken)
        || await db.Set<PaymentRefund>().AnyAsync(
            r => r.StudentId == studentId && r.Status == PaymentRefundStatus.Pending,
            cancellationToken);
}
