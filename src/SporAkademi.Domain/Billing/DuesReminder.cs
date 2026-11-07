using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct DuesReminderId(Guid Value) : IEntityId<DuesReminderId>
{
    public static DuesReminderId From(Guid value) => new(value);
}

/// <summary>Which reminder of a period's due (DEC-355): before its due date, or after it passed unpaid.</summary>
public enum DuesReminderKind
{
    Upcoming,
    Overdue,
}

public enum DuesReminderStatus
{
    /// <summary>Recorded by the daily job; the e-mail goes out after commit (outbox).</summary>
    Pending,
    Sent,

    /// <summary>Not sent: see <see cref="DuesReminderSkipReason"/>.</summary>
    Skipped,
}

public enum DuesReminderSkipReason
{
    /// <summary>The due was paid between the job and the delivery.</summary>
    Paid,

    /// <summary>No guardian or adult student e-mail any more (DEC-356).</summary>
    NoRecipient,
}

/// <summary>When reminders are due (DEC-355): fees are due on <see cref="DueDay"/> of their month.</summary>
public sealed record DuesReminderSchedule
{
    public DuesReminderSchedule(int dueDay, int upcomingDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dueDay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dueDay, 28);
        ArgumentOutOfRangeException.ThrowIfNegative(upcomingDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(upcomingDays, 27);
        DueDay = dueDay;
        UpcomingDays = upcomingDays;
    }

    /// <summary>Day of the month a period's fee is due; at most 28 so every month has it.</summary>
    public int DueDay { get; }

    /// <summary>How many days before the due date the "upcoming" reminder may go out.</summary>
    public int UpcomingDays { get; }

    public DateOnly DueDateOf(YearMonth period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return new DateOnly(period.Year, period.Month, DueDay);
    }

    /// <summary>The reminder an unpaid due of <paramref name="period"/> gets on <paramref name="today"/>, or null when none is due yet.</summary>
    public DuesReminderKind? KindOn(YearMonth period, DateOnly today)
    {
        var dueDate = DueDateOf(period);
        if (today > dueDate)
        {
            return DuesReminderKind.Overdue;
        }

        return dueDate.DayNumber - today.DayNumber <= UpcomingDays ? DuesReminderKind.Upcoming : null;
    }

    /// <summary>The latest period an upcoming reminder can concern on <paramref name="today"/>.</summary>
    public YearMonth LastPeriodOn(DateOnly today) => YearMonth.From(today.AddDays(UpcomingDays));
}

/// <summary>
/// A reminder about one unpaid period of one enrollment (P2-F4, DEC-355): the record that it was (to be) sent. At most
/// one per enrollment, period and kind (unique in the database), which makes the daily job idempotent. Fees and
/// payments stay the source of truth; the reminder keeps the fee only to word the e-mail.
/// </summary>
public sealed class DuesReminder : AggregateRoot<DuesReminderId>, ITenantScoped
{
    private DuesReminder(DuesReminderId id, SchoolId tenantId, EnrollmentId enrollmentId, StudentId studentId, GroupId groupId, YearMonth period, DuesReminderKind kind, DateOnly dueDate, Money fee)
        : base(id)
    {
        TenantId = tenantId;
        EnrollmentId = enrollmentId;
        StudentId = studentId;
        GroupId = groupId;
        Period = period;
        Kind = kind;
        DueDate = dueDate;
        Fee = fee;
        Status = DuesReminderStatus.Pending;
    }

    // For EF Core materialization only.
    private DuesReminder()
        : base(default)
    {
        Period = null!;
        Fee = null!;
    }

    public SchoolId TenantId { get; private set; }

    public EnrollmentId EnrollmentId { get; private set; }

    public StudentId StudentId { get; private set; }

    public GroupId GroupId { get; private set; }

    public YearMonth Period { get; private set; }

    public DuesReminderKind Kind { get; private set; }

    public DateOnly DueDate { get; private set; }

    /// <summary>The group's full fee for the period; the e-mail states what is still unpaid when it is sent.</summary>
    public Money Fee { get; private set; }

    public DuesReminderStatus Status { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public int RecipientCount { get; private set; }

    public DuesReminderSkipReason? SkipReason { get; private set; }

    public static DuesReminder Schedule(SchoolId tenantId, EnrollmentId enrollmentId, StudentId studentId, GroupId groupId, YearMonth period, DuesReminderKind kind, DateOnly dueDate, Money fee)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(fee);
        return new DuesReminder(EntityId.New<DuesReminderId>(), tenantId, enrollmentId, studentId, groupId, period, kind, dueDate, fee);
    }

    /// <summary>Nothing went out yet and the reminder was not skipped.</summary>
    public bool IsPending => Status == DuesReminderStatus.Pending;

    /// <summary>
    /// Records the e-mail to one recipient (each recipient has its own outbox message, DEC-356): the first makes the
    /// reminder <see cref="DuesReminderStatus.Sent"/>, each one counts.
    /// </summary>
    public void RecordDelivery(DateTimeOffset now)
    {
        if (Status == DuesReminderStatus.Skipped)
        {
            throw new InvalidOperationException("A skipped reminder is not delivered.");
        }

        Status = DuesReminderStatus.Sent;
        SentAt ??= now;
        RecipientCount++;
    }

    /// <summary>Gives up before anything went out (paid meanwhile, or no recipient left).</summary>
    public void Skip(DuesReminderSkipReason reason)
    {
        if (!IsPending)
        {
            throw new InvalidOperationException("The reminder was already delivered or skipped.");
        }

        Status = DuesReminderStatus.Skipped;
        SkipReason = reason;
    }
}
