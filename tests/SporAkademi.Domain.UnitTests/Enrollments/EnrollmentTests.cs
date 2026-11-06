using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Enrollments;

public sealed class EnrollmentTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly DateOnly Today = new(2027, 1, 14);

    private static readonly EnrollmentStudent Ali = new(EntityId.New<StudentId>(), StudentStatus.Active, new DateOnly(2015, 5, 1));

    private static ScheduleSlot Slot(DayOfWeek day, int startHour, int endHour) =>
        ScheduleSlot.Create(day, TimeRange.Create(new TimeOnly(startHour, 0), new TimeOnly(endHour, 0)).Value).Value;

    private static EnrollmentGroup Group(string name = "U10", int capacity = 10, bool archived = false, int from = 2015, int to = 2016, params ScheduleSlot[] slots) =>
        new(EntityId.New<GroupId>(), name, archived, BirthYearRange.Create(from, to).Value, capacity, slots);

    private static Result<Enrollment> Enroll(EnrollmentGroup group, int active = 0, EnrollmentStudent? student = null, params EnrollmentGroup[] others) =>
        EnrollmentPolicy.Enroll(School, student ?? Ali, group, active, others, Today);

    [Fact]
    [Trait("Story", "US-17")]
    public void Eligible_student_gets_an_active_enrollment_from_the_start_date()
    {
        var group = Group(slots: Slot(DayOfWeek.Tuesday, 17, 18));

        var enrollment = Enroll(group, active: 9, others: Group("Yüzme", slots: Slot(DayOfWeek.Tuesday, 18, 19))).Value;

        enrollment.Status.ShouldBe(EnrollmentStatus.Active);
        enrollment.StudentId.ShouldBe(Ali.Id);
        enrollment.GroupId.ShouldBe(group.Id);
        enrollment.StartDate.ShouldBe(Today);
        enrollment.EndDate.ShouldBeNull();
        enrollment.TenantId.ShouldBe(School);
    }

    [Fact]
    [Trait("Story", "US-17")]
    public void Full_group_is_rejected()
    {
        Enroll(Group(capacity: 10), active: 10).Error.ShouldBe(EnrollmentErrors.GroupFull);
    }

    [Theory]
    [Trait("Story", "US-17")]
    [InlineData(2016, 2017)]
    [InlineData(2013, 2014)]
    public void Birth_year_outside_the_range_is_rejected_with_the_years(int from, int to)
    {
        var error = Enroll(Group(from: from, to: to)).Error;

        error.Code.ShouldBe("Enrollment.BirthYearOutOfRange");
        error.Arguments["year"].ShouldBe(2015);
        error.Arguments["from"].ShouldBe(from);
        error.Arguments["to"].ShouldBe(to);
    }

    [Fact]
    [Trait("Story", "US-17")]
    public void Already_enrolled_student_is_rejected()
    {
        var group = Group();

        Enroll(group, others: group).Error.ShouldBe(EnrollmentErrors.AlreadyEnrolled);
    }

    [Fact]
    [Trait("Story", "US-17")]
    public void Overlapping_slot_with_another_active_group_names_that_group()
    {
        var group = Group(slots: Slot(DayOfWeek.Monday, 17, 19));

        var error = Enroll(group, others: Group("Yüzme Başlangıç", slots: Slot(DayOfWeek.Monday, 18, 20))).Error;

        error.ShouldBe(EnrollmentErrors.ScheduleConflict("Yüzme Başlangıç"));
        error.Arguments["groupName"].ShouldBe("Yüzme Başlangıç");
    }

    [Fact]
    [Trait("Story", "US-15")]
    public void Student_who_left_cannot_be_enrolled()
    {
        var left = Ali with { Status = StudentStatus.Left };

        Enroll(Group(), student: left).Error.ShouldBe(EnrollmentErrors.StudentNotActive);
    }

    [Fact]
    [Trait("Story", "US-17")]
    public void Archived_group_takes_no_enrollments()
    {
        Enroll(Group(archived: true)).Error.ShouldBe(EnrollmentErrors.GroupArchived);
    }

    [Fact]
    [Trait("Story", "US-18")]
    public void Ending_sets_the_date_once_and_never_before_the_start()
    {
        var enrollment = Enroll(Group()).Value;

        enrollment.End(Today.AddDays(-1)).Error.ShouldBe(EnrollmentErrors.InvalidEndDate);
        enrollment.End(Today).IsSuccess.ShouldBeTrue();

        enrollment.Status.ShouldBe(EnrollmentStatus.Ended);
        enrollment.EndDate.ShouldBe(Today);
        enrollment.End(Today).Error.ShouldBe(EnrollmentErrors.AlreadyEnded);
    }
}
