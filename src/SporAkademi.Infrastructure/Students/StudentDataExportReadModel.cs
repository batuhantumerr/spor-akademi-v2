using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Students.Privacy;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

/// <summary>
/// P4-06: one student's rows in the enrollment, attendance and billing modules for the data export, read like the
/// dashboard and report read models (one query per module, tenant filters apply).
/// </summary>
internal sealed class StudentDataExportReadModel(AppDbContext db) : IStudentDataExportReadModel
{
    public async Task<IReadOnlyList<ExportedEnrollmentDto>> EnrollmentsAsync(StudentId studentId, CancellationToken cancellationToken) =>
        await (
                from enrollment in db.Set<Enrollment>().AsNoTracking()
                join grp in db.Set<Group>().AsNoTracking() on enrollment.GroupId equals grp.Id
                where enrollment.StudentId == studentId
                orderby enrollment.StartDate, enrollment.Id
                select new ExportedEnrollmentDto(enrollment.Id.Value, grp.Id.Value, grp.Name, enrollment.StartDate, enrollment.EndDate, enrollment.Status))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExportedAttendanceDto>> AttendanceAsync(StudentId studentId, CancellationToken cancellationToken) =>
        await (
                from record in db.Set<AttendanceRecord>().AsNoTracking()
                join session in db.Set<Session>().AsNoTracking() on record.SessionId equals session.Id
                join grp in db.Set<Group>().AsNoTracking() on session.GroupId equals grp.Id
                where record.StudentId == studentId
                orderby session.Date, session.StartTime
                select new ExportedAttendanceDto(session.Date, session.StartTime, grp.Id.Value, grp.Name, record.Status))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExportedPaymentDto>> PaymentsAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        var payments = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.StudentId == studentId)
            .OrderBy(p => p.PaidOn).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return [.. payments.Select(p => new ExportedPaymentDto(
            p.Id.Value, p.EnrollmentId.Value, p.Period.ToString(), MoneyDto.From(p.Amount), p.Method, p.PaidOn, p.Status, p.CancelReason, p.CancelledAt))];
    }

    public async Task<IReadOnlyList<ExportedRefundDto>> RefundsAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        var refunds = await db.Set<PaymentRefund>().AsNoTracking()
            .Where(r => r.StudentId == studentId)
            .OrderBy(r => r.RequestedAt).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        return [.. refunds.Select(r => new ExportedRefundDto(
            r.Id.Value, r.PaymentId.Value, MoneyDto.From(r.Amount), r.Status, r.Reason, r.RequestedAt, r.CompletedAt, r.ResolutionNote))];
    }

    public async Task<IReadOnlyList<ExportedOnlinePaymentDto>> OnlinePaymentsAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        var attempts = await db.Set<OnlinePayment>().AsNoTracking()
            .Where(p => p.StudentId == studentId)
            .OrderBy(p => p.StartedAt).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return [.. attempts.Select(p => new ExportedOnlinePaymentDto(
            p.Id.Value, p.EnrollmentId.Value, p.Period.ToString(), MoneyDto.From(p.Amount), p.Provider, p.Status, p.StartedAt, p.CompletedAt))];
    }
}
