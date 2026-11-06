using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Attendance;

[Trait("Story", "US-21")]
public sealed class AttendanceHistoryTests(DatabaseFixture database) : AttendanceTest(database)
{
    private const string January = "from=2027-01-01&to=2027-01-31";

    private async Task RecordSomeAsync()
    {
        await SaveOkAsync(Admin, LastThursday, (Ali, "Present"), (Veli, "Absent"));
        await SaveOkAsync(Admin, LastTuesday, (Ali, "Late"));
        await SaveOkAsync(Admin, ThisMorning, (Ali, "Absent"));
    }

    private static List<(string Key, string? Status)> SessionsOf(JsonElement history) =>
        [.. history.GetProperty("sessions").EnumerateArray().Select(s => (
            s.GetProperty("sessionKey").GetString()!,
            s.GetProperty("status").ValueKind == JsonValueKind.Null ? null : s.GetProperty("status").GetString()))];

    [Fact]
    public async Task A_students_history_lists_each_held_session_and_the_attendance_rate()
    {
        await RecordSomeAsync();

        var history = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?{January}", Ct);

        // Held sessions while enrolled (since 4 January); this evening's has not started yet.
        SessionsOf(history).ShouldBe(
        [
            ("2027-01-05T17:00", null),
            ("2027-01-07T10:00", "Present"),
            ("2027-01-07T17:00", null),
            ("2027-01-12T17:00", "Late"),
            ("2027-01-14T10:00", "Absent"),
        ]);
        history.GetProperty("sessions")[0].GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        var summary = history.GetProperty("summary");
        summary.GetProperty("sessions").GetInt32().ShouldBe(5);
        summary.GetProperty("present").GetInt32().ShouldBe(1);
        summary.GetProperty("late").GetInt32().ShouldBe(1);
        summary.GetProperty("absent").GetInt32().ShouldBe(1);
        summary.GetProperty("notRecorded").GetInt32().ShouldBe(2);
        summary.GetProperty("rate").GetDecimal().ShouldBe(0.6667m);
    }

    [Fact]
    public async Task The_date_range_limits_the_history()
    {
        await RecordSomeAsync();

        var history = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?from=2027-01-12&to=2027-01-12", Ct);

        SessionsOf(history).ShouldBe([("2027-01-12T17:00", "Late")]);
        history.GetProperty("summary").GetProperty("rate").GetDecimal().ShouldBe(1m);
    }

    [Fact]
    public async Task Without_counted_sessions_the_rate_is_empty()
    {
        var history = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?{January}", Ct);

        history.GetProperty("summary").GetProperty("rate").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_coach_sees_a_student_only_through_own_groups()
    {
        var swimming = IdOf(await GroupAsync("Yüzme", 20, Mehmet, Slot("Monday", "17:00", "18:00")));
        await EnrollOkAsync(Ali, swimming, "2027-01-04");
        await RecordSomeAsync();
        (await Admin.PutAsJsonAsync(SessionUrl("2027-01-11T17:00", swimming), Records((Ali, "Present")), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ayse = CoachClient(Ayse);
        using var mehmet = CoachClient(Mehmet);

        var seenByAyse = SessionsOf(await ayse.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?{January}", Ct));
        var seenByMehmet = SessionsOf(await mehmet.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?{January}", Ct));
        var seenByAdmin = SessionsOf(await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?{January}", Ct));

        seenByAyse.Count.ShouldBe(5);
        seenByMehmet.ShouldBe([("2027-01-04T17:00", null), ("2027-01-11T17:00", "Present")]);
        seenByAdmin.Count.ShouldBe(7);
        var veliForMehmet = await mehmet.GetAsync(new Uri($"/api/v1/students/{Veli}/attendance?{January}", UriKind.Relative), Ct);
        veliForMehmet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(veliForMehmet)).ShouldBe("Student.NotFound");
    }

    [Fact]
    public async Task A_group_month_is_a_sessions_by_students_table()
    {
        await RecordSomeAsync();
        using var coach = CoachClient(Ayse);

        var month = await coach.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{GroupId}/attendance?month=2027-01", Ct);

        var sessions = month.GetProperty("sessions").EnumerateArray().ToList();
        sessions.Count.ShouldBe(12); // 4 Tuesdays + 4 Thursdays × 2
        sessions.Select(s => s.GetProperty("sessionKey").GetString()).Take(3).ShouldBe(["2027-01-05T17:00", "2027-01-07T10:00", "2027-01-07T17:00"]);
        sessions.Count(s => s.GetProperty("isRecorded").GetBoolean()).ShouldBe(3);
        var rows = month.GetProperty("students").EnumerateArray().ToDictionary(
            r => r.GetProperty("studentId").GetGuid(),
            r => r.GetProperty("statuses").EnumerateArray().Select(s => s.ValueKind == JsonValueKind.Null ? null : s.GetString()).ToList());
        rows.Keys.ShouldBe([Veli, Ali], ignoreOrder: false); // Çelik before Yılmaz
        rows[Ali].Take(5).ShouldBe([null, "Present", null, "Late", "Absent"]);
        rows[Veli].Take(5).ShouldBe([null, "Absent", null, null, null]);
        rows[Ali].Count.ShouldBe(12);
    }

    [Fact]
    public async Task Another_coach_or_school_cannot_see_the_history()
    {
        using var mehmet = CoachClient(Mehmet);
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(otherSchool);

        (await mehmet.GetAsync(new Uri($"/api/v1/groups/{GroupId}/attendance?month=2027-01", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.GetAsync(new Uri($"/api/v1/groups/{GroupId}/attendance?month=2027-01", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.GetAsync(new Uri($"/api/v1/students/{Ali}/attendance?{January}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("from=2027-01-01", "to", "Field.Required")]
    [InlineData("to=2027-01-01", "from", "Field.Required")]
    [InlineData("from=2027-01-10&to=2027-01-01", "to", "Field.Invalid")]
    [InlineData("from=2026-01-01&to=2027-01-31", "to", "Field.Invalid")]
    public async Task The_student_history_needs_a_valid_range(string query, string field, string code)
    {
        var response = await Admin.GetAsync(new Uri($"/api/v1/students/{Ali}/attendance?{query}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(code);
    }

    [Theory]
    [InlineData("", "Field.Required")]
    [InlineData("?month=2027-13", "YearMonth.Invalid")]
    [InlineData("?month=ocak", "YearMonth.Invalid")]
    public async Task The_group_month_needs_a_valid_month(string query, string code)
    {
        var response = await Admin.GetAsync(new Uri($"/api/v1/groups/{GroupId}/attendance{query}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("month")[0].GetString().ShouldBe(code);
    }

    [Fact]
    [Trait("Story", "US-15")]
    public async Task A_student_who_left_keeps_past_attendance_visible()
    {
        await RecordSomeAsync();
        (await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/leave", new { date = "2027-01-13" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var coach = CoachClient(Ayse);

        var history = SessionsOf(await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}/attendance?{January}", Ct));
        var month = await coach.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{GroupId}/attendance?month=2027-01", Ct);
        var lastTuesday = await coach.GetFromJsonAsync<JsonElement>(SessionUrl(LastTuesday), Ct);

        history.ShouldContain(("2027-01-12T17:00", "Late"));
        history.ShouldContain(("2027-01-14T10:00", "Absent")); // recorded before the leave date was set
        month.GetProperty("students").EnumerateArray().Select(r => r.GetProperty("studentId").GetGuid()).ShouldContain(Ali);
        StatusesOf(lastTuesday)[Ali].ShouldBe("Late");
    }
}
