using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using static SporAkademi.Domain.UnitTests.Groups.GroupTests;

namespace SporAkademi.Domain.UnitTests.Groups;

public sealed class ScheduleServicesTests
{
    private static readonly UserId Ayse = EntityId.New<UserId>();
    private static readonly UserId Mehmet = EntityId.New<UserId>();
    private static readonly FacilityId FieldA = EntityId.New<FacilityId>();
    private static readonly FacilityId Pool = EntityId.New<FacilityId>();

    private static GroupWeek Week(string name, UserId? coach, FacilityId facility, params ScheduleSlot[] slots) =>
        new(EntityId.New<GroupId>(), name, coach, facility, slots);

    [Fact]
    [Trait("Story", "US-10")]
    public void Coach_with_another_group_at_the_same_time_is_unavailable()
    {
        var candidate = Week("U10", Ayse, FieldA, Slot(DayOfWeek.Tuesday, 17, 0, 18, 0));
        var other = Week("U12", Ayse, Pool, Slot(DayOfWeek.Tuesday, 17, 30, 19, 0));

        var result = ScheduleConflictChecker.Check(candidate, [other]);

        result.Error.ShouldBe(ScheduleErrors.CoachUnavailable("U12"));
        result.Error.Arguments["groupName"].ShouldBe("U12");
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Coach_can_run_back_to_back_groups()
    {
        var candidate = Week("U10", Ayse, FieldA, Slot(DayOfWeek.Tuesday, 17, 0, 18, 0));
        var other = Week("U12", Ayse, FieldA, Slot(DayOfWeek.Tuesday, 18, 0, 19, 0));

        ScheduleConflictChecker.Check(candidate, [other]).Value.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Same_facility_at_the_same_time_is_only_a_warning()
    {
        var candidate = Week("U10", Ayse, FieldA, Slot(DayOfWeek.Tuesday, 17, 0, 18, 0), Slot(DayOfWeek.Thursday, 17, 0, 18, 0));
        var other = Week("U12", Mehmet, FieldA, Slot(DayOfWeek.Tuesday, 17, 0, 18, 0), Slot(DayOfWeek.Thursday, 17, 0, 18, 0));

        var warnings = ScheduleConflictChecker.Check(candidate, [other]).Value;

        var warning = warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe("Schedule.FacilityOverlap");
        warning.RelatedId.ShouldBe(other.Id.Value);
        warning.Arguments["groupName"].ShouldBe("U12");
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Coach_conflict_wins_over_facility_warnings()
    {
        var candidate = Week("U10", Ayse, FieldA, Slot(DayOfWeek.Monday, 17, 0, 18, 0));
        var sharesField = Week("A Takımı", Mehmet, FieldA, Slot(DayOfWeek.Monday, 17, 0, 18, 0));
        var sameCoach = Week("B Takımı", Ayse, Pool, Slot(DayOfWeek.Monday, 17, 0, 18, 0));

        ScheduleConflictChecker.Check(candidate, [sharesField, sameCoach]).Error.Code.ShouldBe("Schedule.CoachUnavailable");
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Groups_without_a_coach_never_conflict_on_coach()
    {
        var candidate = Week("U10", null, FieldA, Slot(DayOfWeek.Monday, 17, 0, 18, 0));
        var other = Week("U12", null, Pool, Slot(DayOfWeek.Monday, 17, 0, 18, 0));

        ScheduleConflictChecker.Check(candidate, [other]).Value.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void The_group_itself_is_ignored()
    {
        var candidate = Week("U10", Ayse, FieldA, Slot(DayOfWeek.Monday, 17, 0, 18, 0));

        ScheduleConflictChecker.Check(candidate, [candidate with { Name = "old copy" }]).Value.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-12")]
    public void Calendar_expands_slots_across_a_week_boundary()
    {
        var group = NewGroup();
        group.ReplaceSchedule([Slot(DayOfWeek.Sunday, 10, 0, 11, 0), Slot(DayOfWeek.Monday, 18, 0, 19, 0), Slot(DayOfWeek.Monday, 9, 0, 10, 0)]);

        // 2027-01-16 is a Saturday; the window crosses into the next week.
        var sessions = SessionCalendar.Between(group, new DateOnly(2027, 1, 16), new DateOnly(2027, 1, 18));

        sessions.Select(s => (s.Date, s.Time.ToString())).ShouldBe(
        [
            (new DateOnly(2027, 1, 17), "10:00-11:00"),
            (new DateOnly(2027, 1, 18), "09:00-10:00"),
            (new DateOnly(2027, 1, 18), "18:00-19:00"),
        ]);
        sessions.ShouldAllBe(s => s.GroupId == group.Id);
    }

    [Fact]
    [Trait("Story", "US-12")]
    public void Calendar_of_a_single_day_and_of_an_empty_window()
    {
        var group = NewGroup();
        group.ReplaceSchedule([Slot(DayOfWeek.Thursday, 17, 0, 18, 30)]);

        SessionCalendar.Between(group, new DateOnly(2027, 1, 14), new DateOnly(2027, 1, 14)).ShouldHaveSingleItem().Date.ShouldBe(new DateOnly(2027, 1, 14));
        SessionCalendar.Between(group, new DateOnly(2027, 1, 15), new DateOnly(2027, 1, 20)).ShouldBeEmpty();
        SessionCalendar.Between(group, new DateOnly(2027, 1, 20), new DateOnly(2027, 1, 14)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-12")]
    public void Calendar_keeps_wall_clock_times_around_european_dst_change()
    {
        // Istanbul has no DST since 2016; times are local anyway, so the night Europe switches clocks
        // (2027-03-28) changes nothing.
        var group = NewGroup();
        group.ReplaceSchedule([Slot(DayOfWeek.Saturday, 17, 0, 18, 0), Slot(DayOfWeek.Sunday, 17, 0, 18, 0)]);

        var sessions = SessionCalendar.Between(group, new DateOnly(2027, 3, 27), new DateOnly(2027, 3, 28));

        sessions.Select(s => s.Time.ToString()).ShouldBe(["17:00-18:00", "17:00-18:00"]);
        sessions.Sum(s => s.Time.Duration.TotalMinutes).ShouldBe(120);
    }

    [Fact]
    [Trait("Story", "US-11")]
    public void Archived_group_has_no_sessions_after_its_archive_date()
    {
        var group = NewGroup();
        group.ReplaceSchedule([Slot(DayOfWeek.Thursday, 17, 0, 18, 0)]);
        group.Archive(new DateOnly(2027, 1, 14));

        SessionCalendar.Between(group, new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31)).Select(s => s.Date)
            .ShouldBe([new DateOnly(2027, 1, 7), new DateOnly(2027, 1, 14)]);
    }
}
