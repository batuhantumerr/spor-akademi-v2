using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Billing;

/// <summary>P2-F4 (DEC-355): when a period's fee gets which reminder, and a reminder is delivered or skipped once.</summary>
[Trait("Story", "P2-F4")]
public sealed class DuesReminderTests
{
    private static readonly YearMonth January = YearMonth.Create(2027, 1).Value;
    private static readonly DuesReminderSchedule Schedule = new(dueDay: 10, upcomingDays: 3);

    [Theory]
    [InlineData(6, null)]
    [InlineData(7, DuesReminderKind.Upcoming)]
    [InlineData(10, DuesReminderKind.Upcoming)]
    [InlineData(11, DuesReminderKind.Overdue)]
    [InlineData(31, DuesReminderKind.Overdue)]
    public void The_kind_depends_on_the_due_date(int day, DuesReminderKind? expected)
    {
        Schedule.KindOn(January, new DateOnly(2027, 1, day)).ShouldBe(expected);
        Schedule.DueDateOf(January).ShouldBe(new DateOnly(2027, 1, 10));
    }

    [Fact]
    public void An_early_due_day_makes_next_months_fee_upcoming_at_the_end_of_this_month()
    {
        var schedule = new DuesReminderSchedule(dueDay: 1, upcomingDays: 3);

        schedule.LastPeriodOn(new DateOnly(2027, 1, 29)).ShouldBe(YearMonth.Create(2027, 2).Value);
        schedule.KindOn(YearMonth.Create(2027, 2).Value, new DateOnly(2027, 1, 29)).ShouldBe(DuesReminderKind.Upcoming);
        Schedule.LastPeriodOn(new DateOnly(2027, 1, 27)).ShouldBe(January);
        Schedule.KindOn(YearMonth.Create(2027, 2).Value, new DateOnly(2027, 1, 29)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(29, 3)]
    [InlineData(10, -1)]
    [InlineData(10, 28)]
    public void Invalid_schedules_are_rejected(int dueDay, int upcomingDays) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new DuesReminderSchedule(dueDay, upcomingDays));

    [Fact]
    public void A_reminder_is_delivered_or_skipped_once()
    {
        var sent = NewReminder();
        sent.IsPending.ShouldBeTrue();
        var first = new DateTimeOffset(2027, 1, 7, 6, 0, 0, TimeSpan.Zero);
        sent.RecordDelivery(first);
        sent.RecordDelivery(first.AddMinutes(1));
        sent.Status.ShouldBe(DuesReminderStatus.Sent);
        sent.RecipientCount.ShouldBe(2);
        sent.SentAt.ShouldBe(first);
        Should.Throw<InvalidOperationException>(() => sent.Skip(DuesReminderSkipReason.Paid));

        var skipped = NewReminder();
        skipped.Skip(DuesReminderSkipReason.NoRecipient);
        skipped.Status.ShouldBe(DuesReminderStatus.Skipped);
        skipped.SkipReason.ShouldBe(DuesReminderSkipReason.NoRecipient);
        Should.Throw<InvalidOperationException>(() => skipped.RecordDelivery(first));
    }

    private static DuesReminder NewReminder() =>
        DuesReminder.Schedule(
            SchoolId.From(Guid.CreateVersion7()),
            EnrollmentId.From(Guid.CreateVersion7()),
            StudentId.From(Guid.CreateVersion7()),
            GroupId.From(Guid.CreateVersion7()),
            January,
            DuesReminderKind.Upcoming,
            new DateOnly(2027, 1, 10),
            Money.Try(1250m));
}
