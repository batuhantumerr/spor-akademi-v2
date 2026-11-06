using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Dashboard;

/// <summary>
/// The School Admin dashboard (DEC-210) on a hand-computed dataset. Setup happens on 2027-01-14, February and
/// March; the dashboard is read on Wednesday 2027-03-10, 12:00 in Istanbul.
/// </summary>
public sealed class DashboardTests(DatabaseFixture database) : BillingTest(database)
{
    private static readonly DateTimeOffset February20 = new(2027, 2, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset March10 = new(2027, 3, 10, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// U10 Futbol (capacity 4, Wednesday 17:00): s1, s2 since 2027-01-04; fee 1000 from 2027-01, 1200 from 2027-03.
    /// Yüzme (capacity 2, Wednesday 10:00): s3 since 2027-01-04, s4 from 2027-01-04 to 2027-02-15; fee 500 from 2027-01.
    /// An archived group, a student who left. Payments (Recorded): s1 Jan 1000 (paid Jan), s3 Jan 500 (Jan),
    /// s1 Feb 1000 (Feb), s2 Jan 400 (Feb), s3 Mar 500 (Mar); s2 Mar 1000 (Mar) is cancelled.
    /// Attendance: 2027-02-03 Futbol s1 Late, s2 Excused; 2027-02-10 Yüzme s3 Absent, s4 Present;
    /// 2027-03-03 Futbol s1 Present, s2 Absent; today Yüzme s3 Present.
    /// </summary>
    private async Task<HttpClient> SeedAsync()
    {
        var futbol = IdOf(await GroupAsync("U10 Futbol", 4, Ayse, Slot("Wednesday", "17:00", "18:00")));
        var swimmingSport = await CreateSportAsync(Admin, "Yüzme");
        var swimming = IdOf(await BodyOf(await PostGroupAsync(NewGroup("Yüzme", Mehmet.Id.Value, sportId: swimmingSport, capacity: 2))));
        await ScheduleAsync(await GetGroupAsync(swimming), Slot("Wednesday", "10:00", "11:00"));
        await ArchiveAsync(IdOf(await GroupAsync("Eski Grup", 10)));
        await DefineFeeOkAsync(futbol, "2027-01", "1000.00");
        await DefineFeeOkAsync(swimming, "2027-01", "500.00");

        var (s1, s2, s3, s4) = (await RegisterStudentAsync("Ali"), await RegisterStudentAsync("Veli"), await RegisterStudentAsync("Can"), await RegisterStudentAsync("Ece"));
        var leaver = await RegisterStudentAsync("Deniz");
        (await Admin.PostAsJsonAsync($"/api/v1/students/{leaver}/leave", new { date = Today }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var e1 = IdOf(await EnrollOkAsync(s1, futbol, "2027-01-04"));
        var e2 = IdOf(await EnrollOkAsync(s2, futbol, "2027-01-04"));
        var e3 = IdOf(await EnrollOkAsync(s3, swimming, "2027-01-04"));
        var e4 = IdOf(await EnrollOkAsync(s4, swimming, "2027-01-04"));
        await RecordOkAsync(e1, "2027-01", "1000.00", paidOn: "2027-01-10");
        await RecordOkAsync(e3, "2027-01", "500.00", paidOn: "2027-01-12");

        var february = AdvanceTo(February20);
        await RecordOkAsync(e1, "2027-02", "1000.00", february, "2027-02-20");
        await RecordOkAsync(e2, "2027-01", "400.00", february, "2027-02-05");

        var admin = AdvanceTo(March10);
        (await EndAsync(e4, "2027-02-15", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DefineFeeOkAsync(futbol, "2027-03", "1200.00", admin);
        await RecordOkAsync(e3, "2027-03", "500.00", admin, "2027-03-10");
        var cancelled = await RecordOkAsync(e2, "2027-03", "1000.00", admin, "2027-03-10");
        (await CancelAsync(IdOf(cancelled), "Yanlış kayıt", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SaveAttendanceAsync(admin, futbol, "2027-02-03T17:00", (s1, "Late"), (s2, "Excused"));
        await SaveAttendanceAsync(admin, swimming, "2027-02-10T10:00", (s3, "Absent"), (s4, "Present"));
        await SaveAttendanceAsync(admin, futbol, "2027-03-03T17:00", (s1, "Present"), (s2, "Absent"));
        await SaveAttendanceAsync(admin, swimming, "2027-03-10T10:00", (s3, "Present"));
        return admin;
    }

    private static async Task SaveAttendanceAsync(HttpClient client, Guid group, string key, params (Guid Student, string Status)[] marks)
    {
        var body = new { records = marks.Select(m => new { studentId = m.Student, status = m.Status }).ToArray() };
        (await client.PutAsJsonAsync($"/api/v1/groups/{group}/sessions/{key}/attendance", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> DashboardOf(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    [Fact]
    [Trait("Story", "US-24")]
    public async Task Kpis_match_the_seeded_school()
    {
        var admin = await SeedAsync();

        var dashboard = await DashboardOf(admin);

        dashboard.GetProperty("today").GetString().ShouldBe("2027-03-10");
        dashboard.GetProperty("currentPeriod").GetString().ShouldBe("2027-03");
        var kpis = dashboard.GetProperty("kpis");
        kpis.GetProperty("activeStudents").GetInt32().ShouldBe(4);
        kpis.GetProperty("activeGroups").GetInt32().ShouldBe(2);
        kpis.GetProperty("totalCapacity").GetInt32().ShouldBe(6);
        kpis.GetProperty("activeEnrollments").GetInt32().ShouldBe(3);
        kpis.GetProperty("capacityUtilisation").GetDecimal().ShouldBe(0.5m);
        AmountOf(kpis.GetProperty("collectedThisMonth")).ShouldBe("500.00");
        kpis.GetProperty("collectedThisMonth").GetProperty("currency").GetString().ShouldBe("TRY");
        AmountOf(kpis.GetProperty("outstandingThisMonth")).ShouldBe("2400.00");
        AmountOf(kpis.GetProperty("outstandingTotal")).ShouldBe("5500.00");
        // Last 30 days (from 2027-02-09): present 3, late 0, absent 2.
        kpis.GetProperty("attendanceRateLast30Days").GetDecimal().ShouldBe(0.6m);
    }

    [Fact]
    [Trait("Story", "US-24")]
    public async Task Outstanding_figures_agree_with_the_dues_endpoint()
    {
        var admin = await SeedAsync();

        var dashboard = await DashboardOf(admin);
        var dues = await OutstandingAsync("?pageSize=100", admin);
        var march = await OutstandingAsync("?period=2027-03", admin);

        decimal Sum(JsonElement page) => page.GetProperty("items").EnumerateArray().Sum(i => decimal.Parse(AmountOf(i.GetProperty("remaining")), System.Globalization.CultureInfo.InvariantCulture));
        decimal.Parse(AmountOf(dashboard.GetProperty("kpis").GetProperty("outstandingTotal")), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Sum(dues));
        decimal.Parse(AmountOf(dashboard.GetProperty("kpis").GetProperty("outstandingThisMonth")), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(Sum(march));
    }

    [Fact]
    [Trait("Story", "US-24")]
    public async Task Monthly_series_compares_collected_by_payment_date_with_fees_due()
    {
        var admin = await SeedAsync();

        var months = (await DashboardOf(admin)).GetProperty("monthlyBilling").EnumerateArray()
            .Select(m => $"{m.GetProperty("period").GetString()} {AmountOf(m.GetProperty("collected"))}/{AmountOf(m.GetProperty("due"))}");

        months.ShouldBe(
        [
            "2026-10 0.00/0.00",
            "2026-11 0.00/0.00",
            "2026-12 0.00/0.00",
            "2027-01 1500.00/3000.00",
            "2027-02 1400.00/3000.00",
            "2027-03 500.00/2900.00",
        ]);
    }

    [Fact]
    [Trait("Story", "US-21")]
    public async Task Weekly_attendance_covers_eight_monday_weeks_up_to_today()
    {
        var admin = await SeedAsync();

        var weeks = (await DashboardOf(admin)).GetProperty("weeklyAttendance").EnumerateArray()
            .Select(w => $"{w.GetProperty("weekStart").GetString()} {w.GetProperty("counted").GetInt32()} {(w.GetProperty("rate").ValueKind == JsonValueKind.Null ? "-" : w.GetProperty("rate").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture))}");

        weeks.ShouldBe(
        [
            "2027-01-18 0 -",
            "2027-01-25 0 -",
            "2027-02-01 1 1",
            "2027-02-08 2 0.5",
            "2027-02-15 0 -",
            "2027-02-22 0 -",
            "2027-03-01 2 0.5",
            "2027-03-08 1 1",
        ]);
    }

    [Fact]
    [Trait("Story", "US-17")]
    public async Task Enrollments_by_sport_utilisation_and_todays_sessions()
    {
        var admin = await SeedAsync();

        var dashboard = await DashboardOf(admin);

        dashboard.GetProperty("enrollmentsBySport").EnumerateArray()
            .Select(s => $"{s.GetProperty("sportName").GetString()} {s.GetProperty("activeEnrollments").GetInt32()}")
            .ShouldBe(["Futbol 2", "Yüzme 1"]);
        dashboard.GetProperty("groupUtilisation").EnumerateArray()
            .Select(g => $"{g.GetProperty("groupName").GetString()} {g.GetProperty("activeEnrollments").GetInt32()}/{g.GetProperty("capacity").GetInt32()} {g.GetProperty("utilisation").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .ShouldBe(["U10 Futbol 2/4 0.5", "Yüzme 1/2 0.5"]);
        var today = dashboard.GetProperty("todaySessions");
        today.GetProperty("planned").GetInt32().ShouldBe(2);
        today.GetProperty("recorded").GetInt32().ShouldBe(1);
    }

    [Fact]
    [Trait("Story", "US-24")]
    public async Task Dates_follow_the_school_time_zone_across_a_utc_month_end()
    {
        // Sunday and Monday 09:00 sessions; s1 enrolled since 2027-01-04 with a 1000.00 fee from 2027-01.
        var group = IdOf(await GroupAsync("U10 Futbol", 10, Ayse, Slot("Sunday", "09:00", "10:00"), Slot("Monday", "09:00", "10:00")));
        await DefineFeeOkAsync(group, "2027-01", "1000.00");
        var student = await RegisterStudentAsync("Ali");
        var enrollment = IdOf(await EnrollOkAsync(student, group, "2027-01-04"));
        await RecordOkAsync(enrollment, "2027-01", "400.00", paidOn: "2027-01-10");

        // 22:30 UTC on Sunday 31 January is already 01:30 on Monday 1 February in Istanbul.
        var admin = AdvanceTo(new DateTimeOffset(2027, 1, 31, 22, 30, 0, TimeSpan.Zero));
        await RecordOkAsync(enrollment, "2027-02", "1000.00", admin, "2027-02-01");
        await SaveAttendanceAsync(admin, group, "2027-01-31T09:00", (student, "Present"));

        var dashboard = await DashboardOf(admin);

        dashboard.GetProperty("today").GetString().ShouldBe("2027-02-01");
        dashboard.GetProperty("currentPeriod").GetString().ShouldBe("2027-02");
        AmountOf(dashboard.GetProperty("kpis").GetProperty("collectedThisMonth")).ShouldBe("1000.00");
        var months = dashboard.GetProperty("monthlyBilling").EnumerateArray().ToList();
        months[^1].GetProperty("period").GetString().ShouldBe("2027-02");
        AmountOf(months[^2].GetProperty("collected")).ShouldBe("400.00");
        // The local week starts on Monday 1 February; Sunday's session belongs to the week before.
        var weeks = dashboard.GetProperty("weeklyAttendance").EnumerateArray().ToList();
        weeks[^1].GetProperty("weekStart").GetString().ShouldBe("2027-02-01");
        weeks[^1].GetProperty("counted").GetInt32().ShouldBe(0);
        weeks[^2].GetProperty("weekStart").GetString().ShouldBe("2027-01-25");
        weeks[^2].GetProperty("counted").GetInt32().ShouldBe(1);
        // Today is Monday locally: one planned session (Monday 09:00), nothing recorded yet.
        var today = dashboard.GetProperty("todaySessions");
        today.GetProperty("planned").GetInt32().ShouldBe(1);
        today.GetProperty("recorded").GetInt32().ShouldBe(0);
    }

    [Fact]
    [Trait("Story", "US-19")]
    public async Task A_session_recorded_today_still_counts_after_its_slot_moved()
    {
        // The clock says Thursday 2027-01-14 15:00 in Istanbul; the 10:00 session is over and recorded.
        var group = await GroupAsync("U10 Futbol", 10, Ayse, Slot("Thursday", "10:00", "11:00"));
        var student = await RegisterStudentAsync("Ali");
        await EnrollOkAsync(student, IdOf(group), "2027-01-04");
        await SaveAttendanceAsync(Admin, IdOf(group), "2027-01-14T10:00", (student, "Present"));

        await ScheduleAsync(await GetGroupAsync(IdOf(group)), Slot("Thursday", "17:00", "18:00"));
        var today = (await DashboardOf(Admin)).GetProperty("todaySessions");

        // The moved slot (17:00) plus the recorded 10:00 session: recorded can never exceed planned.
        today.GetProperty("planned").GetInt32().ShouldBe(2);
        today.GetProperty("recorded").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Group_utilisation_lists_the_ten_fullest_groups()
    {
        for (var i = 0; i < 12; i++)
        {
            var group = IdOf(await GroupAsync($"Grup {i:00}", capacity: 12 - i));
            if (i % 3 == 0)
            {
                await EnrollOkAsync(await RegisterStudentAsync($"Öğrenci{i}"), group);
            }
        }

        var groups = (await DashboardOf(Admin)).GetProperty("groupUtilisation").EnumerateArray().ToList();

        groups.Count.ShouldBe(10);
        groups[0].GetProperty("groupName").GetString().ShouldBe("Grup 09");
        groups.Select(g => g.GetProperty("utilisation").GetDecimal()).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task An_empty_school_has_zeros_and_no_ratios()
    {
        var dashboard = await DashboardOf(Admin);

        var kpis = dashboard.GetProperty("kpis");
        kpis.GetProperty("activeStudents").GetInt32().ShouldBe(0);
        kpis.GetProperty("capacityUtilisation").ValueKind.ShouldBe(JsonValueKind.Null);
        kpis.GetProperty("attendanceRateLast30Days").ValueKind.ShouldBe(JsonValueKind.Null);
        AmountOf(kpis.GetProperty("outstandingTotal")).ShouldBe("0.00");
        dashboard.GetProperty("monthlyBilling").GetArrayLength().ShouldBe(6);
        dashboard.GetProperty("weeklyAttendance").GetArrayLength().ShouldBe(8);
        dashboard.GetProperty("enrollmentsBySport").GetArrayLength().ShouldBe(0);
        dashboard.GetProperty("todaySessions").GetProperty("planned").GetInt32().ShouldBe(0);
    }

    [Fact]
    [Trait("Story", "US-02")]
    public async Task Each_school_sees_only_its_own_numbers()
    {
        var admin = await SeedAsync();
        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        using var adminB = Api.SchoolAdminClient(schoolB);
        var studentOfB = await RegisterStudentAsync("Bora", client: adminB);
        var sportOfB = await CreateSportAsync(adminB, "Futbol");
        var facilityOfB = await CreateFacilityAsync(adminB, "B Sahası");
        var groupOfB = IdOf(await BodyOf(await PostGroupAsync(NewGroup("B Grubu", sportId: sportOfB, facilityId: facilityOfB, capacity: 5), adminB)));
        (await EnrollAsync(studentOfB, groupOfB, client: adminB)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var a = (await DashboardOf(admin)).GetProperty("kpis");
        var b = await DashboardOf(adminB);

        a.GetProperty("activeStudents").GetInt32().ShouldBe(4);
        a.GetProperty("totalCapacity").GetInt32().ShouldBe(6);
        b.GetProperty("kpis").GetProperty("activeStudents").GetInt32().ShouldBe(1);
        b.GetProperty("kpis").GetProperty("activeEnrollments").GetInt32().ShouldBe(1);
        AmountOf(b.GetProperty("kpis").GetProperty("outstandingTotal")).ShouldBe("0.00");
        b.GetProperty("enrollmentsBySport").EnumerateArray().Single().GetProperty("sportId").GetGuid().ShouldBe(sportOfB);
        b.GetProperty("weeklyAttendance").EnumerateArray().ShouldAllBe(w => w.GetProperty("counted").GetInt32() == 0);
    }

    [Fact]
    public async Task Only_school_admins_get_the_dashboard()
    {
        using var coach = CoachClient(Ayse);

        (await coach.GetAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
