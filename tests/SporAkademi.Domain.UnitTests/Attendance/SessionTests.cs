using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Attendance;

public sealed class SessionTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly GroupId Group = EntityId.New<GroupId>();
    private static readonly DateOnly Thursday = new(2027, 1, 14);
    private static readonly TimeRange TenToEleven = TimeRange.Create(new TimeOnly(10, 0), new TimeOnly(11, 0)).Value;
    private static readonly UserId Coach = EntityId.New<UserId>();
    private static readonly UserId Admin = EntityId.New<UserId>();
    private static readonly StudentId Ali = EntityId.New<StudentId>();
    private static readonly StudentId Veli = EntityId.New<StudentId>();
    private static readonly DateTimeOffset At = new(2027, 1, 14, 9, 0, 0, TimeSpan.Zero);

    private static Session NewSession() => Session.Open(School, Group, Thursday, TenToEleven);

    private static DateTime Local(int day, int hour, int minute = 0) => new(2027, 1, day, hour, minute, 0);

    private static AttendanceMark[] Marks(params (StudentId Student, AttendanceStatus Status)[] marks) =>
        [.. marks.Select(m => new AttendanceMark(m.Student, m.Status))];

    [Fact]
    [Trait("Story", "US-19")]
    public void Saving_records_each_student_with_who_and_when()
    {
        var session = NewSession();

        session.Save(Marks((Ali, AttendanceStatus.Present), (Veli, AttendanceStatus.Excused)), new Recorder(Coach, false), Local(14, 12), At).IsSuccess.ShouldBeTrue();

        session.StartTime.ShouldBe(new TimeOnly(10, 0));
        session.EndTime.ShouldBe(new TimeOnly(11, 0));
        session.Records.Select(r => (r.StudentId, r.Status, r.RecordedBy, r.RecordedAt)).ShouldBe(
            [(Ali, AttendanceStatus.Present, Coach, At), (Veli, AttendanceStatus.Excused, Coach, At)], ignoreOrder: true);
        session.Changes.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Story", "US-19")]
    [InlineData(9, 59, false)]
    [InlineData(10, 0, true)] // the session has started
    public void A_session_that_has_not_started_cannot_be_recorded(int hour, int minute, bool allowed)
    {
        var saved = NewSession().Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Admin, true), Local(14, hour, minute), At);

        saved.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            saved.Error.ShouldBe(AttendanceErrors.SessionInFuture);
        }
    }

    [Fact]
    [Trait("Story", "US-19")]
    public void Saving_the_same_marks_again_changes_nothing()
    {
        var session = NewSession();
        session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Coach, false), Local(14, 12), At);

        session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Admin, true), Local(14, 13), At.AddHours(1)).IsSuccess.ShouldBeTrue();

        session.Records.ShouldHaveSingleItem().RecordedBy.ShouldBe(Coach);
        session.Changes.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-20")]
    public void A_correction_keeps_the_previous_status_with_who_and_when()
    {
        var session = NewSession();
        session.Save(Marks((Ali, AttendanceStatus.Absent)), new Recorder(Coach, false), Local(14, 12), At);

        session.Save(Marks((Ali, AttendanceStatus.Late)), new Recorder(Admin, true), Local(15, 9), At.AddDays(1)).IsSuccess.ShouldBeTrue();

        var record = session.Records.ShouldHaveSingleItem();
        record.Status.ShouldBe(AttendanceStatus.Late);
        record.RecordedBy.ShouldBe(Admin);
        var change = session.Changes.ShouldHaveSingleItem();
        (change.StudentId, change.PreviousStatus, change.NewStatus, change.ChangedBy, change.ChangedAt)
            .ShouldBe((Ali, AttendanceStatus.Absent, AttendanceStatus.Late, Admin, At.AddDays(1)));
    }

    [Theory]
    [Trait("Story", "US-20")]
    [InlineData(16, 11, 0, true)] // exactly 48 hours after the end (11:00)
    [InlineData(16, 11, 1, false)]
    public void A_coach_may_only_edit_within_48_hours_after_the_end(int day, int hour, int minute, bool allowed)
    {
        var session = NewSession();
        session.Save(Marks((Ali, AttendanceStatus.Absent)), new Recorder(Coach, false), Local(14, 12), At);

        var saved = session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Coach, false), Local(day, hour, minute), At.AddDays(2));

        saved.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            saved.Error.ShouldBe(AttendanceErrors.EditWindowExpired);
            session.Records.Single().Status.ShouldBe(AttendanceStatus.Absent);
        }
    }

    [Theory]
    [Trait("Story", "US-20")]
    [InlineData(1, 31, 22, 59, "SessionInFuture")] // a minute before the 23:00 start
    [InlineData(1, 31, 23, 0, null)]
    [InlineData(2, 2, 23, 59, null)] // exactly 48 hours after the 23:59 end, two days into February
    [InlineData(2, 3, 0, 0, "EditWindowExpired")]
    public void A_late_night_session_rolls_its_window_over_midnight_and_the_month_end(int month, int day, int hour, int minute, string? error)
    {
        var lateNight = Session.Open(School, Group, new DateOnly(2027, 1, 31), TimeRange.Create(new TimeOnly(23, 0), new TimeOnly(23, 59)).Value);

        var saved = lateNight.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Coach, false), new DateTime(2027, month, day, hour, minute, 0), At);

        saved.IsSuccess.ShouldBe(error is null);
        if (error is not null)
        {
            saved.Error.Code.ShouldBe($"Attendance.{error}");
        }
    }

    [Fact]
    [Trait("Story", "US-20")]
    public void After_the_window_an_unchanged_coach_save_is_still_refused()
    {
        var session = NewSession();
        session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Coach, false), Local(14, 12), At);

        session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Coach, false), Local(16, 11, 1), At.AddDays(2))
            .Error.ShouldBe(AttendanceErrors.EditWindowExpired);
    }

    [Fact]
    [Trait("Story", "US-20")]
    public void An_admin_may_edit_at_any_time()
    {
        var session = NewSession();

        session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Admin, true), Local(31, 23), At.AddDays(17)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    [Trait("Story", "US-19")]
    public void A_student_twice_or_an_unknown_status_is_invalid()
    {
        NewSession().Save(Marks((Ali, AttendanceStatus.Present), (Ali, AttendanceStatus.Absent)), new Recorder(Coach, false), Local(14, 12), At)
            .Error.ShouldBe(CommonErrors.FieldInvalid);
        NewSession().Save(Marks((Ali, (AttendanceStatus)9)), new Recorder(Coach, false), Local(14, 12), At)
            .Error.ShouldBe(CommonErrors.FieldInvalid);
    }

    [Theory]
    [Trait("Story", "US-19")]
    [InlineData("2027-01-14T17:00", true)]
    [InlineData("2027-01-14T7:00", false)]
    [InlineData("2027-02-30T17:00", false)]
    [InlineData("2027-01-14 17:00", false)]
    [InlineData("1999-12-31T17:00", false)]
    [InlineData("2027-01-14T17:00:00", false)]
    public void Session_keys_are_yyyy_mm_dd_t_hh_mm(string key, bool valid)
    {
        var parsed = SessionKey.Parse(key);

        parsed.IsSuccess.ShouldBe(valid);
        if (valid)
        {
            parsed.Value.ShouldBe(new SessionKey(Thursday, new TimeOnly(17, 0)));
            parsed.Value.ToString().ShouldBe(key);
        }
        else
        {
            parsed.Error.ShouldBe(AttendanceErrors.InvalidSessionKey);
        }
    }

    [Fact]
    [Trait("Story", "P3-08")]
    public void A_save_raises_attendance_recorded_for_the_students_whose_status_changed_only()
    {
        var session = NewSession();
        session.Save(Marks((Ali, AttendanceStatus.Present), (Veli, AttendanceStatus.Absent)), new Recorder(Coach, false), Local(14, 12), At);
        var first = session.DomainEvents.OfType<AttendanceRecorded>().ShouldHaveSingleItem();
        first.Marks.ShouldBe(Marks((Ali, AttendanceStatus.Present), (Veli, AttendanceStatus.Absent)));
        (first.TenantId, first.SessionId, first.GroupId, first.Date).ShouldBe((School, session.Id, Group, Thursday));
        session.ClearDomainEvents();

        // The same statuses again: nothing changed, nothing to tell.
        session.Save(Marks((Ali, AttendanceStatus.Present)), new Recorder(Coach, false), Local(14, 12), At);
        session.DomainEvents.ShouldBeEmpty();

        // A correction tells only about the corrected child.
        session.Save(Marks((Ali, AttendanceStatus.Present), (Veli, AttendanceStatus.Late)), new Recorder(Coach, false), Local(14, 12), At);
        session.DomainEvents.OfType<AttendanceRecorded>().ShouldHaveSingleItem().Marks.ShouldBe(Marks((Veli, AttendanceStatus.Late)));
    }
}
