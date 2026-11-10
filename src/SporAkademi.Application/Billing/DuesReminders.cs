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

    /// <summary>Off by default: reminders start only once an operator turns them on with a <see cref="StartsOn"/> date.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Go-live date (DEC-355): periods whose due date is before it are never reminded, so the first run does not
    /// e-mail old debts. Required when <see cref="Enabled"/>.
    /// </summary>
    public DateOnly? StartsOn { get; set; }

    /// <summary>Day of the month a period's fee is due (1–28).</summary>
    public int DueDay { get; set; } = 10;

    /// <summary>The "upcoming" reminder goes out at most this many days before the due date.</summary>
    public int UpcomingDays { get; set; } = 3;

    /// <summary>Overdue reminders cover the current period and this many before it (older debts are not e-mailed).</summary>
    public int OverdueLookbackMonths { get; set; } = 2;

    public DuesReminderSchedule Schedule => new(DueDay, UpcomingDays);

    public bool IsValid() =>
        DueDay is >= 1 and <= 28 && UpcomingDays is >= 0 and <= 27 && OverdueLookbackMonths is >= 0 and <= 24
        && (!Enabled || StartsOn is not null);
}

/// <summary>
/// P2-F4 (DEC-355): records the fee reminders due today for the current school and queues their e-mails. Run by the
/// daily job once per school (in that school's background context). Idempotent: a reminder exists at most once per
/// enrollment, period and kind, so running again the same day (or after a crash) adds nothing.
/// </summary>
public sealed record SendDuesRemindersCommand : ICommand<int>;

public interface IDuesReminderRepository
{
    /// <summary>Reminders already recorded for these enrollments in the periods <paramref name="firstPeriod"/> – <paramref name="lastPeriod"/>.</summary>
    Task<IReadOnlySet<(EnrollmentId Enrollment, YearMonth Period, DuesReminderKind Kind)>> ExistingAsync(
        IReadOnlyCollection<EnrollmentId> enrollmentIds,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken);

    void Add(DuesReminder reminder);

    /// <summary>Tracked, for recording the delivery.</summary>
    Task<DuesReminder?> GetAsync(DuesReminderId id, CancellationToken cancellationToken);
}

internal sealed class SendDuesRemindersHandler(
    DueCharges dueCharges,
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
        if (!settings.Enabled || settings.StartsOn is not { } startsOn || !await tenancy.IsSchoolActiveAsync(school, cancellationToken))
        {
            return Result.Success(0);
        }

        var today = await tenancy.TodayAsync(school, cancellationToken);
        var schedule = settings.Schedule;
        var last = schedule.LastPeriodOn(today);
        var first = YearMonth.From(YearMonth.From(today).FirstDay.AddMonths(-settings.OverdueLookbackMonths));
        var book = await dueCharges.ForSchoolAsync(first, last, cancellationToken);
        if (book.Charges.Count == 0)
        {
            return Result.Success(0);
        }

        var paid = await payments.RecordedTotalsAsync(null, first, last, cancellationToken);
        var enrollmentOf = book.Enrollments;
        // What is owed after the fee rules (DEC-373): a discounted sibling is reminded of the discounted amount.
        var due = FeeRules.Outstanding(book.Charges, paid)
            .Select(d => d.Charge)
            // Never before go-live, and not for a period whose due date passed before the enrollment even started.
            .Where(d => schedule.DueDateOf(d.Period) >= startsOn && enrollmentOf[d.Enrollment.Id].StartDate <= schedule.DueDateOf(d.Period))
            .Select(d => (Due: d, Kind: schedule.KindOn(d.Period, today)))
            .Where(d => d.Kind is not null)
            .ToList();
        if (due.Count == 0)
        {
            return Result.Success(0);
        }

        var existing = await reminders.ExistingAsync([.. due.Select(d => d.Due.Enrollment.Id).Distinct()], first, last, cancellationToken);
        var reachable = await contacts.GetAsync([.. due.Select(d => d.Due.StudentId.Value).Distinct()], today, cancellationToken);

        var recorded = 0;
        foreach (var (item, kind) in due)
        {
            var student = item.StudentId;
            // Without a recipient nothing is recorded, so a guardian e-mail added later still gets the reminder.
            if (existing.Contains((item.Enrollment.Id, item.Period, kind!.Value)) || !reachable.TryGetValue(student.Value, out var to))
            {
                continue;
            }

            var reminder = DuesReminder.Schedule(school, item.Enrollment.Id, student, item.Enrollment.GroupId, item.Period, kind.Value, schedule.DueDateOf(item.Period), item.Fee);
            reminders.Add(reminder);
            // One message per recipient (DEC-356): a retry after a failure re-sends to that recipient only.
            foreach (var recipient in to.Recipients)
            {
                outbox.Enqueue(new DuesReminderMessage(reminder.Id.Value, recipient.Id), school.Value);
            }

            recorded++;
        }

        return Result.Success(recorded);
    }
}

/// <summary>
/// Delivers one fee reminder to one recipient (DEC-355, DEC-356): it re-reads the payments and the recipients when it
/// runs, so a due paid (or an address removed) in the meantime is not sent, and records the outcome. Each recipient has
/// its own message, so a failure for one never re-sends to the others; for the same recipient delivery is at least
/// once (SMTP accepted, recording failed: the retry sends again).
/// </summary>
internal sealed partial class SendDuesReminderEmail(
    IUnitOfWork unitOfWork,
    IDuesReminderRepository reminders,
    IPaymentRepository payments,
    IEnrollmentDirectory enrollments,
    DueCharges dueCharges,
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
        if (reminder is null || reminder.Status == DuesReminderStatus.Skipped)
        {
            LogNothingToDo(logger, message.ReminderId, context.MessageId);
            return;
        }

        // What is owed now under the fee rules (DEC-373), not the amount when the reminder was scheduled.
        var enrollment = await enrollments.FindAsync(reminder.EnrollmentId, cancellationToken);
        var owed = enrollment is null ? null : await dueCharges.ForEnrollmentAsync(enrollment, reminder.Period, cancellationToken);
        var remaining = (owed?.Fee.Amount ?? 0m) - await payments.RecordedTotalAsync(reminder.EnrollmentId, reminder.Period, cancellationToken);
        var today = await tenancy.TodayAsync(school, cancellationToken);
        var contactsOfStudent = (await contacts.GetAsync([reminder.StudentId.Value], today, cancellationToken)).GetValueOrDefault(reminder.StudentId.Value);
        var recipient = contactsOfStudent?.Recipients.FirstOrDefault(r => r.Id == message.RecipientId);
        if (remaining <= 0 || recipient is null)
        {
            // Nothing went out for this reminder yet: record why. Otherwise only this recipient is left out.
            var reason = remaining <= 0 ? DuesReminderSkipReason.Paid : DuesReminderSkipReason.NoRecipient;
            if (reminder.IsPending && (reason == DuesReminderSkipReason.Paid || contactsOfStudent is null))
            {
                await RecordAsync(reminder.Id, r => { if (r.IsPending) { r.Skip(reason); } }, cancellationToken);
            }

            LogSkipped(logger, reminder.Id.Value, reason, context.MessageId);
            return;
        }

        var group = (await groups.GetManyAsync([reminder.GroupId.Value], cancellationToken)).GetValueOrDefault(reminder.GroupId.Value);
        var schoolName = await tenancy.SchoolNameAsync(school, cancellationToken) ?? "Spor Akademi";
        await emailSender.SendAsync(DuesReminderEmails.Compose(reminder, remaining, recipient, contactsOfStudent!, group?.Name, schoolName), cancellationToken);
        await RecordAsync(reminder.Id, r => r.RecordDelivery(timeProvider.GetUtcNow()), cancellationToken);
        LogSent(logger, reminder.Id.Value, context.MessageId);
    }

    private async Task RecordAsync(DuesReminderId id, Action<DuesReminder> change, CancellationToken cancellationToken)
    {
        var saved = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var tracked = await reminders.GetAsync(id, ct);
                if (tracked is not null && tracked.Status != DuesReminderStatus.Skipped)
                {
                    change(tracked);
                }

                return Result.Success(Unit.Value);
            },
            cancellationToken);
        if (saved.IsFailure)
        {
            // Retried by the outbox; this recipient's e-mail may then go out again (at least once).
            throw new InvalidOperationException($"Recording reminder {id.Value} failed with {saved.Error.Code}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dues reminder {ReminderId} sent to one recipient (outbox message {MessageId})")]
    private static partial void LogSent(ILogger logger, Guid reminderId, Guid messageId);

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
