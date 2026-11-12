using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Abstractions.Realtime;
using SporAkademi.Application.Announcements.Contracts;
using SporAkademi.Application.Attendance.Contracts;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Realtime;

/// <summary>
/// P3-08 (DEC-396, DEC-398): recipients are worked out when the committed message is delivered, from the data as it is
/// then (the group's coach today, the children enrolled today, their active parent accounts), never from a group
/// membership a client chose or a connection remembered. A message of a school only ever reaches users of that school.
/// </summary>
internal static class RealtimeRecipients
{
    public static Guid TenantOf(OutboxMessageContext context) =>
        context.TenantId ?? throw new InvalidOperationException("Realtime messages belong to a school.");

    public static async Task<IReadOnlyList<Guid>> ParentsOfAsync(IStudentParentAccounts parents, IReadOnlyCollection<StudentId> students, CancellationToken cancellationToken) =>
        students.Count == 0
            ? []
            : [.. (await parents.ListAsync(students, cancellationToken)).Select(p => p.UserId).Distinct()];
}

/// <summary>A new announcement: to the group's coach, the parents of the children enrolled in it today and the School Admins.</summary>
internal sealed class NotifyAnnouncementPublished(
    IGroupDirectory groups,
    IEnrollmentHistory enrollments,
    ITenancyModule tenancy,
    IStudentParentAccounts parents,
    IRealtimeNotifier notifier) : IOutboxMessageHandler<AnnouncementPublishedMessage>
{
    public async Task Handle(AnnouncementPublishedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = RealtimeRecipients.TenantOf(context);
        var notice = new RealtimeEvent(RealtimeEventTypes.AnnouncementPublished, message.AnnouncementId, GroupId: message.GroupId);

        // The admins' lists show every group's announcements, archived ones too.
        await notifier.NotifyRoleAsync(tenant, Role.SchoolAdmin, notice, cancellationToken);

        var group = await groups.FindAsync(GroupId.From(message.GroupId), cancellationToken);
        if (group is null || group.IsArchived)
        {
            // Neither the coach nor parents see an archived group's announcements (US-25, DEC-385).
            return;
        }

        var today = await tenancy.TodayAsync(SchoolId.From(tenant), cancellationToken);
        var children = (await enrollments.ListOfGroupAsync(GroupId.From(message.GroupId), today, today, cancellationToken))
            .Where(p => p.Covers(today))
            .Select(p => p.StudentId)
            .Distinct()
            .ToList();
        var recipients = new HashSet<Guid>(await RealtimeRecipients.ParentsOfAsync(parents, children, cancellationToken));
        if (group.CoachId is { } coach)
        {
            recipients.Add(coach);
        }

        if (recipients.Count > 0)
        {
            await notifier.NotifyUsersAsync(tenant, recipients, notice, cancellationToken);
        }
    }
}

/// <summary>A child's attendance: to that child's parents only ("bugün antrenmana katıldı / katılmadı").</summary>
internal sealed class NotifyAttendanceRecorded(IStudentParentAccounts parents, IRealtimeNotifier notifier) : IOutboxMessageHandler<AttendanceRecordedMessage>
{
    public async Task Handle(AttendanceRecordedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = RealtimeRecipients.TenantOf(context);
        var recipients = await RealtimeRecipients.ParentsOfAsync(parents, [StudentId.From(message.StudentId)], cancellationToken);
        if (recipients.Count == 0)
        {
            return;
        }

        var notice = new RealtimeEvent(
            RealtimeEventTypes.AttendanceRecorded,
            message.SessionId,
            GroupId: message.GroupId,
            StudentId: message.StudentId,
            Date: message.Date,
            Status: message.Status.ToString());
        await notifier.NotifyUsersAsync(tenant, recipients, notice, cancellationToken);
    }
}

/// <summary>A payment (by hand or online): to that child's parents only.</summary>
internal sealed class NotifyPaymentRecorded(IStudentParentAccounts parents, IRealtimeNotifier notifier) : IOutboxMessageHandler<PaymentRecordedMessage>
{
    public async Task Handle(PaymentRecordedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = RealtimeRecipients.TenantOf(context);
        var recipients = await RealtimeRecipients.ParentsOfAsync(parents, [StudentId.From(message.StudentId)], cancellationToken);
        if (recipients.Count == 0)
        {
            return;
        }

        var notice = new RealtimeEvent(RealtimeEventTypes.PaymentRecorded, message.PaymentId, GroupId: message.GroupId, StudentId: message.StudentId);
        await notifier.NotifyUsersAsync(tenant, recipients, notice, cancellationToken);
    }
}

/// <summary>P3-13: a refunded payment, to that child's parents only (the parent app reloads dues and payments).</summary>
internal sealed class NotifyPaymentRefunded(IStudentParentAccounts parents, IRealtimeNotifier notifier) : IOutboxMessageHandler<PaymentRefundedMessage>
{
    public async Task Handle(PaymentRefundedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var tenant = RealtimeRecipients.TenantOf(context);
        var recipients = await RealtimeRecipients.ParentsOfAsync(parents, [StudentId.From(message.StudentId)], cancellationToken);
        if (recipients.Count == 0)
        {
            return;
        }

        var notice = new RealtimeEvent(RealtimeEventTypes.PaymentRefunded, message.PaymentId, GroupId: message.GroupId, StudentId: message.StudentId);
        await notifier.NotifyUsersAsync(tenant, recipients, notice, cancellationToken);
    }
}
