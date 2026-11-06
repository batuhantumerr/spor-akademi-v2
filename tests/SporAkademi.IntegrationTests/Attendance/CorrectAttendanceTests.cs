using System.Net;
using System.Net.Http.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Attendance;

[Trait("Story", "US-20")]
public sealed class CorrectAttendanceTests(DatabaseFixture database) : AttendanceTest(database)
{
    // This morning's session ends at 11:00 in Istanbul (08:00 UTC); a coach may edit until 08:00 UTC on the 16th.
    private static readonly DateTimeOffset WindowEnd = new(2027, 1, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_correction_keeps_the_previous_status_who_changed_it_and_when()
    {
        using (var coach = CoachClient(Ayse))
        {
            await SaveOkAsync(coach, ThisMorning, (Ali, "Absent"), (Veli, "Present"));
        }

        AdvanceTo(new DateTimeOffset(2027, 1, 15, 9, 30, 0, TimeSpan.Zero));
        using var admin = Api.SchoolAdminClient(School);
        var corrected = await SaveOkAsync(admin, ThisMorning, (Ali, "Late"), (Veli, "Present"));

        StatusesOf(corrected)[Ali].ShouldBe("Late");
        var change = (await RowsAsync("SELECT student_id, previous_status, new_status, changed_by, changed_at FROM attendance.attendance_changes")).ShouldHaveSingleItem();
        change[0].ShouldBe(Ali);
        change[1].ShouldBe("Absent");
        change[2].ShouldBe("Late");
        change[3].ShouldNotBe(Ayse.Id.Value);
        change[4].ShouldBe(new DateTime(2027, 1, 15, 9, 30, 0, DateTimeKind.Utc));
        var record = (await RowsAsync($"SELECT status, recorded_at FROM attendance.attendance_records WHERE student_id = '{Ali}'")).ShouldHaveSingleItem();
        record[0].ShouldBe("Late");
    }

    [Fact]
    public async Task A_coach_can_correct_until_48_hours_after_the_session_ends()
    {
        using (var coach = CoachClient(Ayse))
        {
            await SaveOkAsync(coach, ThisMorning, (Ali, "Absent"));
        }

        AdvanceTo(WindowEnd);
        using var inTime = CoachClient(Ayse);
        (await SaveAsync(inTime, ThisMorning, (Ali, "Present"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        AdvanceTo(WindowEnd.AddMinutes(1));
        using var late = CoachClient(Ayse);
        var response = await SaveAsync(late, ThisMorning, (Ali, "Late"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(response)).ShouldBe("Attendance.EditWindowExpired");
        (await CountAsync("SELECT count(*) FROM attendance.attendance_changes")).ShouldBe(1);
    }

    [Fact]
    public async Task After_the_window_even_an_identical_coach_save_is_forbidden()
    {
        // DEC-171: the window is checked before comparing statuses, so a resent unchanged PUT is 403 too.
        using (var coach = CoachClient(Ayse))
        {
            await SaveOkAsync(coach, ThisMorning, (Ali, "Present"));
        }

        AdvanceTo(WindowEnd.AddMinutes(1));
        using var late = CoachClient(Ayse);
        var response = await SaveAsync(late, ThisMorning, (Ali, "Present"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(response)).ShouldBe("Attendance.EditWindowExpired");
    }

    [Fact]
    public async Task After_48_hours_only_a_school_admin_can_change_attendance()
    {
        AdvanceTo(WindowEnd.AddDays(30));
        using var coach = CoachClient(Ayse);
        using var admin = Api.SchoolAdminClient(School);

        (await SaveAsync(coach, ThisMorning, (Ali, "Present"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SaveAsync(admin, ThisMorning, (Ali, "Present"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SaveAsync(admin, ThisMorning, (Ali, "Excused"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM attendance.attendance_changes")).ShouldBe(1);
    }

    [Fact]
    public async Task An_admin_can_correct_an_archived_groups_history_but_its_coach_cannot_see_it()
    {
        await SaveOkAsync(Admin, LastTuesday, (Ali, "Absent"));
        (await ArchiveAsync(GroupId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var coach = CoachClient(Ayse);

        var corrected = await SaveOkAsync(Admin, LastTuesday, (Ali, "Excused"));
        var coachGet = await coach.GetAsync(new Uri(SessionUrl(LastTuesday), UriKind.Relative), Ct);

        StatusesOf(corrected)[Ali].ShouldBe("Excused");
        coachGet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // The archive day's morning session is still part of the history (DEC-170).
        (await Admin.GetAsync(new Uri(SessionUrl(ThisMorning), UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        StatusesOf(await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(ThisMorning), Ct)).Keys.ShouldBe([Veli, Ali], ignoreOrder: true);
    }
}
