using System.Net;
using System.Net.Http.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Attendance;

[Trait("Story", "US-19")]
public sealed class TakeAttendanceTests(DatabaseFixture database) : AttendanceTest(database)
{
    [Fact]
    public async Task A_coach_opening_a_held_session_sees_the_students_enrolled_that_day()
    {
        var later = await RegisterStudentAsync("Can", "Arslan");
        await EnrollOkAsync(later, GroupId, "2027-01-13");
        using var coach = CoachClient(Ayse);

        var lastThursday = await coach.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(LastThursday), Ct);
        var today = await coach.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(ThisMorning), Ct);

        StatusesOf(lastThursday).ShouldBe(new SortedDictionary<Guid, string?> { [Ali] = null, [Veli] = null });
        today.GetProperty("students").EnumerateArray().Select(s => s.GetProperty("studentId").GetGuid())
            .ShouldBe([later, Veli, Ali]); // Arslan, Çelik, Yılmaz (Turkish order)
        today.GetProperty("isRecorded").GetBoolean().ShouldBeFalse();
        today.GetProperty("start").GetString().ShouldBe("10:00");
        today.GetProperty("end").GetString().ShouldBe("11:00");
        today.GetProperty("sessionKey").GetString().ShouldBe(ThisMorning);
    }

    [Fact]
    [Trait("Story", "US-14")]
    public async Task The_list_carries_only_id_name_and_status()
    {
        using var coach = CoachClient(Ayse);

        var list = await coach.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(ThisMorning), Ct);

        list.GetProperty("students")[0].EnumerateObject().Select(p => p.Name).ShouldBe(["studentId", "firstName", "lastName", "status"]);
        var json = list.GetRawText();
        json.ShouldNotContain("+90");
        json.ShouldNotContain("@");
    }

    [Fact]
    public async Task Saved_attendance_keeps_who_recorded_it_and_when()
    {
        using var coach = CoachClient(Ayse);

        var saved = await SaveOkAsync(coach, ThisMorning, (Ali, "Present"), (Veli, "Late"));

        StatusesOf(saved).ShouldBe(new SortedDictionary<Guid, string?> { [Ali] = "Present", [Veli] = "Late" });
        saved.GetProperty("isRecorded").GetBoolean().ShouldBeTrue();
        var rows = await RowsAsync("SELECT recorded_by, recorded_at FROM attendance.attendance_records");
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => (Guid)r[0]! == Ayse.Id.Value);
        rows.ShouldAllBe(r => ((DateTime)r[1]!) == new DateTime(2027, 1, 14, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Attendance_cannot_be_taken_for_a_session_that_has_not_started()
    {
        using var coach = CoachClient(Ayse);

        var response = await SaveAsync(coach, ThisEvening, (Ali, "Present"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Attendance.SessionInFuture");
        (await CountAsync("SELECT count(*) FROM attendance.sessions")).ShouldBe(0);
    }

    [Fact]
    public async Task A_session_starts_counting_as_held_at_its_start_time()
    {
        AdvanceTo(new DateTimeOffset(2027, 1, 14, 14, 0, 0, TimeSpan.Zero)); // 17:00 in Istanbul
        using var coach = CoachClient(Ayse);

        (await SaveAsync(coach, ThisEvening, (Ali, "Present"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Saving_again_updates_the_same_session_without_copies_or_changes()
    {
        using var coach = CoachClient(Ayse);
        await SaveOkAsync(coach, ThisMorning, (Ali, "Present"), (Veli, "Absent"));

        var again = await SaveOkAsync(coach, ThisMorning, (Ali, "Present"), (Veli, "Absent"));

        StatusesOf(again).ShouldBe(new SortedDictionary<Guid, string?> { [Ali] = "Present", [Veli] = "Absent" });
        (await CountAsync("SELECT count(*) FROM attendance.sessions")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM attendance.attendance_records")).ShouldBe(2);
        (await CountAsync("SELECT count(*) FROM attendance.attendance_changes")).ShouldBe(0);
    }

    [Fact]
    public async Task Students_not_mentioned_keep_their_status()
    {
        using var coach = CoachClient(Ayse);
        await SaveOkAsync(coach, ThisMorning, (Ali, "Present"));

        var saved = await SaveOkAsync(coach, ThisMorning, (Veli, "Excused"));

        StatusesOf(saved).ShouldBe(new SortedDictionary<Guid, string?> { [Ali] = "Present", [Veli] = "Excused" });
    }

    [Fact]
    public async Task Twenty_students_are_saved_in_one_request()
    {
        // NFR-PRF-03: the phone flow is one GET and one PUT for the whole group.
        var students = new List<Guid> { Ali, Veli };
        for (var i = 0; i < 18; i++)
        {
            var id = await RegisterStudentAsync($"Öğrenci{i:00}", "Deneme");
            await EnrollOkAsync(id, GroupId, "2027-01-04");
            students.Add(id);
        }

        using var coach = CoachClient(Ayse);

        var saved = await SaveOkAsync(coach, ThisMorning, [.. students.Select(s => (s, "Present"))]);

        StatusesOf(saved).Values.ShouldAllBe(s => s == "Present");
        StatusesOf(saved).Count.ShouldBe(20);
    }

    [Theory]
    [InlineData("2027-01-13T10:00")] // a Wednesday: no training
    [InlineData("2027-01-14T10:30")] // Thursday, but not a start time
    public async Task A_key_outside_the_schedule_is_not_found(string key)
    {
        using var coach = CoachClient(Ayse);

        var get = await coach.GetAsync(new Uri(SessionUrl(key), UriKind.Relative), Ct);
        var put = await SaveAsync(coach, key, (Ali, "Present"));

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(get)).ShouldBe("Attendance.SessionNotInSchedule");
        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(put)).ShouldBe("Attendance.SessionNotInSchedule");
    }

    [Theory]
    [InlineData("2027-01-14")]
    [InlineData("2027-13-14T10:00")]
    [InlineData("bugun")]
    public async Task A_malformed_key_is_rejected(string key)
    {
        using var coach = CoachClient(Ayse);

        var response = await SaveAsync(coach, key, (Ali, "Present"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("Attendance.InvalidSessionKey");
    }

    [Fact]
    public async Task Only_students_enrolled_on_the_session_date_can_be_recorded()
    {
        var other = await RegisterStudentAsync("Can", "Arslan");
        var later = await RegisterStudentAsync("Ece", "Kaya");
        await EnrollOkAsync(later, GroupId, "2027-01-13");
        using var coach = CoachClient(Ayse);

        var stranger = await SaveAsync(coach, ThisMorning, (Ali, "Present"), (other, "Present"));
        var notYet = await SaveAsync(Admin, LastThursday, (later, "Present"));

        stranger.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(stranger)).ShouldBe("Attendance.StudentNotEnrolled");
        var notYetBody = await BodyOf(notYet);
        notYetBody.GetProperty("code").GetString().ShouldBe("Attendance.StudentNotEnrolled", notYetBody.GetRawText());
        notYetBody.GetProperty("args").GetProperty("studentId").GetGuid().ShouldBe(later);
        (await CountAsync("SELECT count(*) FROM attendance.attendance_records")).ShouldBe(0);
    }

    [Fact]
    public async Task A_student_is_enrolled_through_the_day_the_enrollment_ends()
    {
        var enrollments = await StudentEnrollmentsAsync(Veli);
        (await EndAsync(enrollments[0].GetProperty("id").GetGuid(), "2027-01-12")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var coach = CoachClient(Ayse);

        StatusesOf(await coach.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(LastTuesday), Ct)).Keys.ShouldContain(Veli);
        StatusesOf(await coach.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(ThisMorning), Ct)).Keys.ShouldNotContain(Veli);
        (await SaveAsync(coach, LastTuesday, (Veli, "Present"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_same_day_transfer_lists_the_student_only_in_the_new_group()
    {
        var u10Enrollment = (await StudentEnrollmentsAsync(Veli))[0].GetProperty("id").GetGuid();
        (await EndAsync(u10Enrollment, Today)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var swimming = IdOf(await GroupAsync("Yüzme", 20, null, Slot("Thursday", "12:00", "13:00")));
        await EnrollOkAsync(Veli, swimming, Today);

        var u10Today = StatusesOf(await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(ThisMorning), Ct));
        var swimmingToday = StatusesOf(await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl("2027-01-14T12:00", swimming), Ct));
        var u10Tuesday = StatusesOf(await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(LastTuesday), Ct));
        var put = await SaveAsync(Admin, ThisMorning, (Veli, "Present"));
        var history = await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/students/{Veli}/attendance?from={Today}&to={Today}", Ct);

        u10Today.Keys.ShouldNotContain(Veli);
        swimmingToday.Keys.ShouldBe([Veli]);
        u10Tuesday.Keys.ShouldContain(Veli); // the earlier days of the old enrollment stay
        put.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(put)).ShouldBe("Attendance.StudentNotEnrolled");
        history.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("groupName").GetString()).ShouldBe(["Yüzme"]);
    }

    [Fact]
    public async Task On_an_end_day_without_transfer_the_list_may_exceed_the_capacity()
    {
        // DEC-170: the seat is free from the end date (US-18), but the leaving student still trains that day.
        var mini = IdOf(await GroupAsync("Mini", 2, null, Slot("Thursday", "12:00", "13:00")));
        var (first, second, newcomer) = (await RegisterStudentAsync("Bir", "Deneme"), await RegisterStudentAsync("İki", "Deneme"), await RegisterStudentAsync("Üç", "Deneme"));
        await EnrollOkAsync(first, mini, "2027-01-04");
        var leaving = (await EnrollOkAsync(second, mini, "2027-01-04")).GetProperty("id").GetGuid();
        (await EndAsync(leaving, Today)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnrollOkAsync(newcomer, mini, Today);

        var list = StatusesOf(await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl("2027-01-14T12:00", mini), Ct));

        list.Keys.ShouldBe([first, second, newcomer], ignoreOrder: true);
    }

    [Fact]
    public async Task Invalid_records_are_field_errors()
    {
        using var coach = CoachClient(Ayse);

        var missing = await coach.PutAsJsonAsync(SessionUrl(ThisMorning), new { }, Ct);
        var twice = await SaveAsync(coach, ThisMorning, (Ali, "Present"), (Ali, "Absent"));

        (await BodyOf(missing)).GetProperty("errors").GetProperty("records")[0].GetString().ShouldBe("Field.Required");
        (await BodyOf(twice)).GetProperty("errors").GetProperty("records")[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task Other_coaches_and_other_schools_cannot_reach_the_session()
    {
        using var mehmet = CoachClient(Mehmet);
        var otherSchool = await Infrastructure.TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(otherSchool);

        foreach (var client in new[] { mehmet, otherAdmin })
        {
            var get = await client.GetAsync(new Uri(SessionUrl(ThisMorning), UriKind.Relative), Ct);
            var put = await SaveAsync(client, ThisMorning, (Ali, "Present"));
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(put)).ShouldBe("Group.NotFound");
        }
    }

    [Fact]
    public async Task A_school_admin_can_take_attendance_for_any_group()
    {
        var saved = await SaveOkAsync(Admin, ThisMorning, (Ali, "Present"));

        StatusesOf(saved)[Ali].ShouldBe("Present");
    }

    [Fact]
    public async Task Concurrent_saves_of_one_session_end_in_one_session_with_every_record()
    {
        var students = new List<Guid> { Ali, Veli };
        for (var i = 0; i < 2; i++)
        {
            var id = await RegisterStudentAsync($"Eş{i}", "Zamanlı");
            await EnrollOkAsync(id, GroupId, "2027-01-04");
            students.Add(id);
        }

        var clients = new[] { CoachClient(Ayse), Api.SchoolAdminClient(School), CoachClient(Ayse), Api.SchoolAdminClient(School) };

        var responses = await Task.WhenAll(clients.Select((c, i) => SaveAsync(c, ThisMorning, (students[i], "Present"))));

        responses.Select(r => r.StatusCode).ShouldAllBe(s => s == HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM attendance.sessions")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM attendance.attendance_records")).ShouldBe(4);
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_recorded_session_keeps_its_time_after_the_schedule_changes()
    {
        await SaveOkAsync(Admin, ThisMorning, (Ali, "Present"));
        var group = await GetGroupAsync(GroupId);
        (await PutScheduleAsync(group, null, Slot("Thursday", "10:00", "12:00"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>(SessionUrl(ThisMorning), Ct);

        list.GetProperty("end").GetString().ShouldBe("11:00");
        (await Admin.GetAsync(new Uri(SessionUrl(LastTuesday), UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
