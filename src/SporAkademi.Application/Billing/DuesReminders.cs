using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Notifications;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>Configuration section <c>Billing:DuesReminders</c> (DEC-355).</summary>
public sealed class DuesReminderSettings
{
    public const string Section = "Billing:DuesReminders";

    /// <summary>Day of the month a period's fee is due (1–28).</summary>
    public int DueDay { get; set; } = 10;

    /// <summary>The "upcoming" reminder goes out at most this many days before the due date.</summary>
    public int UpcomingDays { get; set; } = 3;

    /// <summary>Overdue reminders cover the current period and this many before it (older debts are not e-mailed).</summary>
    public int OverdueLookbackMonths { get; set; } = 2;

    public DuesReminderSchedule Schedule => new(DueDay, UpcomingDays);

    public bool IsValid() => DueDay is >= 1 and <= 28 && UpcomingDays is >= 0 and <= 27 && OverdueLookbackMonths is >= 0 and <= 24;
}

/// <summary>
/// P2-F4 (DEC-355): records the fee reminders due today for the current school and queues their e-mails. Run by the
/// daily job once per school (in that school's background context). Idempotent: a reminder exists at most once per
/// enrollment, period and kind, so running again the same day (or after a crash) adds nothing.
/// </summary>
public sealed record SendDuesRemindersCommand : ICommand<int>;

public interface IDuesReminderRepository
{
    /// <summary>Reminders already recorded for these enrollments from <paramref name="firstPeriod"/> on.</summary>
    Task<IReadOnlySet<(EnrollmentId Enrollment, YearMonth Period, DuesReminderKind Kind)>> ExistingAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        YearMonth firstPeriod,
        CancellationToken cancellationToken);

    void Add(DuesReminder reminder);

    /// <summary>Tracked, for recording the delivery.</summary>
    Task<DuesReminder?> GetAsync(DuesReminderId id, CancellationToken cancellationToken);
}

internal sealed class SendDuesRemindersHandler(
    IEnrollmentDirectory enrollments,
    IGroupFeeSchedule fees,
    IPaymentRepository payments,
    IStudentReminderContacts contacts,
    IDuesReminderRepository reminders,
    ITenancyModule tenancy,
    IOutbox outbox,
    ICurrentUser currentUser,
    DuesReminderSettings settings) : ICommandHandler<SendDuesRemindersCommand, int>
{
    public async Task<Result<int>> Handle(SendDuesRemindersCommand command, CancellationToken cancellationToken)
    {
        var school = currentUser.RequireTenant();
        if (!await tenancy.IsSchoolActiveAsync(school, cancellationToken))
        {
            return Result.Success(0);
        }

        var today = await tenancy.TodayAsync(school, cancellationToken);
        var schedule = settings.Schedule;
        var last = schedule.LastPeriodOn(today);
        var first = YearMonth.From(YearMonth.From(today).FirstDay.AddMonths(-settings.OverdueLookbackMonths));
        var active = await enrollments.ListActiveBetweenAsync(null, first.FirstDay, last.LastDay, cancellationToken);
        if (active.Count == 0)
        {
            return Result.Success(0);
        }

        var groupFees = await fees.GetFeesAsync([.. active.Select(e => e.GroupId).Distinct()], cancellationToken);
        var rates = groupFees.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<FeeRate>)[.. g.Value.Select(f => new FeeRate(f.EffectiveFrom, f.Amount))]);
        var paid = await payments.RecordedTotalsAsync(null, first, last, cancellationToken);
        var due = DueCalculator.Outstanding(
                active.Select(e => new DueEnrollment(e.Id, e.GroupId, e.StartDate, e.EndDate)),
                rates,
                paid,
                last,
                first)
            .Select(d => (Due: d, Kind: schedule.KindOn(d.Period, today)))
            .Where(d => d.Kind is not null)
            .ToList();
        if (due.Count == 0)
        {
            return Result.Success(0);
        }

        var studentOf = active.ToDictionary(e => e.Id, e => e.StudentId);
        var existing = await reminders.ExistingAsync([.. due.Select(d => d.Due.EnrollmentId).Distinct()], first, cancellationToken);
        var reachable = await contacts.GetAsync([.. due.Select(d => studentOf[d.Due.EnrollmentId].Value).Distinct()], today, cancellationToken);

        var recorded = 0;
        foreach (var (item, kind) in due)
        {
            var student = studentOf[item.EnrollmentId];
            // Without a recipient nothing is recorded, so a guardian e-mail added later still gets the reminder.
            if (existing.Contains((item.EnrollmentId, item.Period, kind!.Value)) || !reachable.ContainsKey(student.Value))
            {
                continue;
            }

            var reminder = DuesReminder.Schedule(school, item.EnrollmentId, student, item.GroupId, item.Period, kind.Value, schedule.DueDateOf(item.Period), item.Fee);
            reminders.Add(reminder);
            outbox.Enqueue(new DuesReminderMessage(reminder.Id.Value), school.Value);
            recorded++;
        }

        return Result.Success(recorded);
    }
}

/// <summary>
/// Delivers one fee reminder (DEC-355, DEC-356): it re-reads the payments and the recipients when it runs, so a due
/// paid (or an address removed) in the meantime is skipped, and records the outcome. Delivery is at least once: a
/// retry after a partial failure can send a recipient the same reminder again.
/// </summary>
internal sealed partial class SendDuesReminderEmail(
    IUnitOfWork unitOfWork,
    IDuesReminderRepository reminders,
    IPaymentRepository payments,
    IStudentReminderContacts contacts,
    IGroupDirectory groups,
    ITenancyModule tenancy,
    IEmailSender emailSender,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<SendDuesReminderEmail> logger) : IOutboxMessageHandler<DuesReminderMessage>
{
    public async Task Handle(DuesReminderMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        var school = currentUser.RequireTenant();
        var reminder = await reminders.GetAsync(DuesReminderId.From(message.ReminderId), cancellationToken);
        if (reminder is null || !reminder.IsPending)
        {
            LogNothingToDo(logger, message.ReminderId, context.MessageId);
            return;
        }

        var remaining = reminder.Fee.Amount - await payments.RecordedTotalAsync(reminder.EnrollmentId, reminder.Period, cancellationToken);
        var today = await tenancy.TodayAsync(school, cancellationToken);
        var recipients = (await contacts.GetAsync([reminder.StudentId.Value], today, cancellationToken)).GetValueOrDefault(reminder.StudentId.Value);
        DuesReminderSkipReason? skip = remaining <= 0 ? DuesReminderSkipReason.Paid
            : recipients is null ? DuesReminderSkipReason.NoRecipient
            : null;

        if (skip is null)
        {
            var group = (await groups.GetManyAsync([reminder.GroupId.Value], cancellationToken)).GetValueOrDefault(reminder.GroupId.Value);
            var schoolName = await tenancy.SchoolNameAsync(school, cancellationToken) ?? "Spor Akademi";
            foreach (var recipient in recipients!.Recipients)
            {
                await emailSender.SendAsync(DuesReminderEmails.Compose(reminder, remaining, recipient, recipients, group?.Name, schoolName), cancellationToken);
            }
        }

        var saved = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var tracked = await reminders.GetAsync(reminder.Id, ct);
                if (tracked is not { IsPending: true })
                {
                    return Result.Success(Unit.Value);
                }

                if (skip is { } reason)
                {
                    tracked.Skip(reason);
                }
                else
                {
                    tracked.MarkSent(recipients!.Recipients.Count, timeProvider.GetUtcNow());
                }

                return Result.Success(Unit.Value);
            },
            cancellationToken);
        if (saved.IsFailure)
        {
            // Retried by the outbox; the e-mail may then go out again (at least once).
            throw new InvalidOperationException($"Recording reminder {reminder.Id.Value} failed with {saved.Error.Code}.");
        }

        if (skip is { } skipped)
        {
            LogSkipped(logger, reminder.Id.Value, skipped, context.MessageId);
        }
        else
        {
            LogSent(logger, reminder.Id.Value, recipients!.Recipients.Count, context.MessageId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dues reminder {ReminderId} sent to {Recipients} recipient(s) (outbox message {MessageId})")]
    private static partial void LogSent(ILogger logger, Guid reminderId, int recipients, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dues reminder {ReminderId} skipped: {Reason} (outbox message {MessageId})")]
    private static partial void LogSkipped(ILogger logger, Guid reminderId, DuesReminderSkipReason reason, Guid messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dues reminder {ReminderId} already handled (outbox message {MessageId})")]
    private static partial void LogNothingToDo(ILogger logger, Guid reminderId, Guid messageId);
}

/// <summary>The Turkish fee reminder e-mails (DEC-350): who, which group and month, what is unpaid, and the due date.</summary>
internal static class DuesReminderEmails
{
    public const string UpcomingTemplate = "billing.dues-upcoming";
    public const string OverdueTemplate = "billing.dues-overdue";

    private static readonly string[] MonthNames =
        ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    public static EmailMessage Compose(DuesReminder reminder, decimal remaining, ReminderRecipient recipient, ReminderContacts student, string? groupName, string schoolName)
    {
        var period = $"{MonthNames[reminder.Period.Month - 1]} {reminder.Period.Year}";
        var group = groupName is null ? string.Empty : $" ({groupName} grubu)";
        var overdue = reminder.Kind == DuesReminderKind.Overdue;
        var body = new EmailBody($"Merhaba {recipient.FirstName} {recipient.LastName},")
            .Paragraph(overdue
                ? $"{student.FirstName} {student.LastName} için {schoolName}{group} {period} aidatının {EmailBody.Lira(remaining)} tutarındaki kısmı, {EmailBody.Date(reminder.DueDate)} olan son ödeme tarihi geçtiği halde ödenmemiş görünüyor."
                : $"{student.FirstName} {student.LastName} için {schoolName}{group} {period} aidatının son ödeme tarihi {EmailBody.Date(reminder.DueDate)}. Ödenmemiş tutar: {EmailBody.Lira(remaining)}.")
            .Paragraph("Ödemeyi okulunuza nakit veya havale ile yapabilirsiniz. Ödemeyi yaptıysanız bu e-postayı dikkate almayın; kaydı okulunuz günceller.")
            .Paragraph("Sorularınız için okulunuzla iletişime geçin.");
        var subject = overdue ? $"{period} aidatı: ödeme tarihi geçti" : $"{period} aidatı: son ödeme tarihi yaklaşıyor";
        return body.Build(overdue ? OverdueTemplate : UpcomingTemplate, recipient.Email, subject);
    }
}
