using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Groups;

public sealed class GroupTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly SportId Football = EntityId.New<SportId>();
    private static readonly FacilityId FieldA = EntityId.New<FacilityId>();
    private static readonly UserId Coach = EntityId.New<UserId>();

    internal static ScheduleSlot Slot(DayOfWeek day, int startHour, int startMinute, int endHour, int endMinute) =>
        ScheduleSlot.Create(day, TimeRange.Create(new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute)).Value).Value;

    internal static Group NewGroup(string name = "U10 Futbol", UserId? coach = null, FacilityId? facility = null) =>
        Group.Create(School, Football, facility ?? FieldA, coach ?? Coach, name, BirthYearRange.Create(2015, 2016).Value, 20).Value;

    [Theory]
    [Trait("Story", "US-09")]
    [InlineData(2015, 2016)]
    [InlineData(2015, 2015)]
    [InlineData(1900, 2100)]
    public void Birth_year_range_accepts_ordered_years(int from, int to)
    {
        var range = BirthYearRange.Create(from, to).Value;

        range.From.ShouldBe(from);
        range.To.ShouldBe(to);
        range.Contains(from).ShouldBeTrue();
        range.Contains(to + 1).ShouldBeFalse();
    }

    [Theory]
    [Trait("Story", "US-09")]
    [InlineData(2017, 2016)]
    [InlineData(1899, 2000)]
    [InlineData(2000, 2101)]
    public void Birth_year_range_rejects_reversed_or_absurd_years(int from, int to)
    {
        BirthYearRange.Create(from, to).Error.ShouldBe(BirthYearRange.Invalid);
    }

    [Fact]
    [Trait("Story", "US-09")]
    public void Group_is_created_active_with_a_tidy_name_and_turkish_key()
    {
        var group = NewGroup("  İleri   Yüzme ");

        group.Status.ShouldBe(GroupStatus.Active);
        group.Name.ShouldBe("İleri Yüzme");
        group.NameKey.ShouldBe("ileri yüzme");
        group.TenantId.ShouldBe(School);
        group.CoachId.ShouldBe(Coach);
        group.ScheduleSlots.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Story", "US-09")]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1001)]
    public void Capacity_must_be_at_least_one(int capacity)
    {
        Group.Create(School, Football, FieldA, Coach, "U10", BirthYearRange.Create(2015, 2016).Value, capacity)
            .Error.ShouldBe(GroupErrors.InvalidCapacity);
    }

    [Fact]
    [Trait("Story", "US-09")]
    public void Name_is_required()
    {
        Group.Create(School, Football, FieldA, null, "  ", BirthYearRange.Create(2015, 2016).Value, 10)
            .Error.ShouldBe(CommonErrors.FieldRequired);
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Adjacent_slots_do_not_overlap_half_open()
    {
        var group = NewGroup();

        var result = group.ReplaceSchedule([Slot(DayOfWeek.Tuesday, 17, 0, 18, 0), Slot(DayOfWeek.Tuesday, 18, 0, 19, 0)]);

        result.IsSuccess.ShouldBeTrue();
        group.ScheduleSlots.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Overlapping_slots_of_one_group_are_rejected_and_the_schedule_is_kept()
    {
        var group = NewGroup();
        group.ReplaceSchedule([Slot(DayOfWeek.Monday, 9, 0, 10, 0)]);

        var result = group.ReplaceSchedule([Slot(DayOfWeek.Tuesday, 17, 0, 18, 30), Slot(DayOfWeek.Tuesday, 18, 0, 19, 0)]);

        result.Error.ShouldBe(ScheduleErrors.SlotOverlap);
        group.ScheduleSlots.ShouldBe([Slot(DayOfWeek.Monday, 9, 0, 10, 0)]);
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Same_time_on_different_days_does_not_overlap()
    {
        Slot(DayOfWeek.Monday, 17, 0, 18, 0).Overlaps(Slot(DayOfWeek.Wednesday, 17, 0, 18, 0)).ShouldBeFalse();
        Slot(DayOfWeek.Monday, 17, 0, 18, 0).Overlaps(Slot(DayOfWeek.Monday, 17, 59, 18, 30)).ShouldBeTrue();
    }

    [Fact]
    [Trait("Story", "US-10")]
    public void Slots_are_kept_in_weekly_order()
    {
        var group = NewGroup();

        group.ReplaceSchedule([Slot(DayOfWeek.Thursday, 17, 0, 18, 0), Slot(DayOfWeek.Monday, 18, 0, 19, 0), Slot(DayOfWeek.Monday, 9, 0, 10, 0)]);

        group.ScheduleSlots.Select(s => s.ToString()).ShouldBe(["Monday 09:00-10:00", "Monday 18:00-19:00", "Thursday 17:00-18:00"]);
    }

    [Fact]
    [Trait("Story", "US-11")]
    public void Archive_raises_the_event_with_the_schools_date_once()
    {
        var group = NewGroup();
        var today = new DateOnly(2027, 1, 14);

        group.Archive(today).IsSuccess.ShouldBeTrue();

        group.Status.ShouldBe(GroupStatus.Archived);
        group.ArchivedOn.ShouldBe(today);
        group.DomainEvents.ShouldBe([new GroupArchived(group.Id, School, today)]);
        group.Archive(today).Error.ShouldBe(GroupErrors.Archived);
    }

    [Fact]
    [Trait("Story", "US-11")]
    public void Archived_group_cannot_be_changed()
    {
        var group = NewGroup();
        group.Archive(new DateOnly(2027, 1, 14));

        group.Update("Yeni", BirthYearRange.Create(2014, 2015).Value, 30, FieldA, Coach).Error.ShouldBe(GroupErrors.Archived);
        group.ReplaceSchedule([Slot(DayOfWeek.Monday, 9, 0, 10, 0)]).Error.ShouldBe(GroupErrors.Archived);
    }

    [Fact]
    [Trait("Story", "US-11")]
    public void Update_changes_the_editable_fields()
    {
        var group = NewGroup();
        var otherFacility = EntityId.New<FacilityId>();

        group.Update("U11 Futbol", BirthYearRange.Create(2014, 2015).Value, 25, otherFacility, null).IsSuccess.ShouldBeTrue();

        group.Name.ShouldBe("U11 Futbol");
        group.BirthYearRange.ShouldBe(BirthYearRange.Create(2014, 2015).Value);
        group.Capacity.ShouldBe(25);
        group.FacilityId.ShouldBe(otherFacility);
        group.CoachId.ShouldBeNull();
        group.SportId.ShouldBe(Football);
    }

    [Fact]
    [Trait("Story", "US-08")]
    public void Clearing_the_coach_keeps_the_group()
    {
        var group = NewGroup();

        group.ClearCoach();

        group.CoachId.ShouldBeNull();
        group.Status.ShouldBe(GroupStatus.Active);
    }
}
