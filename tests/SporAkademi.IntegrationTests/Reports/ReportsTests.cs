using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Reports;

/// <summary>
/// P3-09 School Admin reports (DEC-406 – DEC-411) on a hand-computed school. Setup runs on 2027-01-14, 2027-02-02,
/// 2027-02-20 and 2027-03-10 (Istanbul); the reports are read on Wednesday 2027-03-10, 12:00, for 2027-01 – 2027-03.
/// </summary>
[Trait("Story", "P3-09")]
public sealed class ReportsTests(DatabaseFixture database) : BillingTest(database)
{
    private const string Range = "from=2027-01&to=2027-03";

    private int _phones = 700;

    private Seeded _s = null!;

    /// <summary>
    /// Groups (sport, coach, capacity, fee): G1 "U10 Futbol" (Futbol, Ayşe, 4, 1000), G2 "Yüzme" (Yüzme, Mehmet, 2, 500),
    /// G3 "U12 Futbol" (Futbol, no coach, 10, 800), "Eski Grup" (Futbol, 10, no fee; archived 2027-02-20) and "Yeni Grup"
    /// (Futbol, 8, created 2027-03-10, empty). Enrollments from 2027-01-04: s1, s2, s6 in G1; s3 and s4 (ended 2027-02-15)
    /// in G2; s5 in G3; s7 in Eski Grup. s5 and s6 share a guardian with fee rules 10 % / 15 % from 2027-01, so s5 pays 720
    /// (s6's 1000 ranks first). January is closed on 2027-02-02, then s5's link is removed: January stays 720, February and
    /// March are 800. Payments (Recorded): s1 Jan 1000 (paid 01-10), s3 Jan 500 (01-12), s5 Jan 720 (01-12), s1 Feb 1000
    /// (02-20), s2 Jan 400 (02-05), s3 Mar 500 (03-10); s2 Mar 1000 is cancelled. Attendance: 01-13 G1 s1 P, s2 A, s6 P;
    /// 02-03 G1 s1 L, s2 E, s6 P; 02-10 G2 s3 A, s4 P; 03-03 G1 s1 P, s2 A, s6 A; 03-10 G2 s3 P.
    /// </summary>
    private async Task<HttpClient> SeedAsync()
    {
        var g1 = IdOf(await GroupAsync("U10 Futbol", 4, Ayse, Slot("Wednesday", "17:00", "18:00")));
        var swimming = await CreateSportAsync(Admin, "Yüzme");
        var g2 = IdOf(await BodyOf(await PostGroupAsync(NewGroup("Yüzme", Mehmet.Id.Value, sportId: swimming, capacity: 2))));
        await ScheduleAsync(await GetGroupAsync(g2), Slot("Wednesday", "10:00", "11:00"));
        var g3 = IdOf(await GroupAsync("U12 Futbol", 10));
        var old = IdOf(await GroupAsync("Eski Grup", 10));
        await DefineFeeOkAsync(g1, "2027-01", "1000.00");
        await DefineFeeOkAsync(g2, "2027-01", "500.00");
        await DefineFeeOkAsync(g3, "2027-01", "800.00");
        (await Admin.PostAsJsonAsync("/api/v1/settings/billing-rules", new { effectiveFrom = "2027-01", secondChildPercent = 10, thirdAndLaterChildPercent = 15, singleChargeOnGroupChange = true }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var (s1, s2, s3, s4) = (await RegisterStudentAsync("Ali"), await RegisterStudentAsync("Veli"), await RegisterStudentAsync("Can"), await RegisterStudentAsync("Ece"));
        var (s6, s6Guardians) = await RegisterWithGuardiansAsync("Zeynep", NewGuardianLink("Ayşe"));
        var (s5, s5Guardians) = await RegisterWithGuardiansAsync("Mert", new { guardianId = s6Guardians[0], relationship = "Mother" }, NewGuardianLink("Fatma"));
        var s7 = await RegisterStudentAsync("Deniz");
        var e1 = IdOf(await EnrollOkAsync(s1, g1, "2027-01-04"));
        var e2 = IdOf(await EnrollOkAsync(s2, g1, "2027-01-04"));
        var e3 = IdOf(await EnrollOkAsync(s3, g2, "2027-01-04"));
        var e4 = IdOf(await EnrollOkAsync(s4, g2, "2027-01-04"));
        var e5 = IdOf(await EnrollOkAsync(s5, g3, "2027-01-04"));
        await EnrollOkAsync(s6, g1, "2027-01-04");
        await EnrollOkAsync(s7, old, "2027-01-04");
        await RecordOkAsync(e1, "2027-01", "1000.00", paidOn: "2027-01-10");
        await RecordOkAsync(e3, "2027-01", "500.00", paidOn: "2027-01-12");
        await RecordOkAsync(e5, "2027-01", "720.00", paidOn: "2027-01-12");
        await SaveAttendanceAsync(Admin, g1, "2027-01-13T17:00", (s1, "Present"), (s2, "Absent"), (s6, "Present"));

        var february = AdvanceTo(new DateTimeOffset(2027, 2, 2, 9, 0, 0, TimeSpan.Zero));
        (await RunCloseJobAsync()).ShouldBe(1);
        await ReplaceGuardiansAsync(s5, february, s5Guardians.Single(g => g != s6Guardians[0]));

        february = AdvanceTo(new DateTimeOffset(2027, 2, 20, 9, 0, 0, TimeSpan.Zero));
        await RecordOkAsync(e1, "2027-02", "1000.00", february, "2027-02-20");
        await RecordOkAsync(e2, "2027-01", "400.00", february, "2027-02-05");
        (await ArchiveAsync(old, february)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EndAsync(e4, "2027-02-15", february)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SaveAttendanceAsync(february, g1, "2027-02-03T17:00", (s1, "Late"), (s2, "Excused"), (s6, "Present"));
        await SaveAttendanceAsync(february, g2, "2027-02-10T10:00", (s3, "Absent"), (s4, "Present"));

        var admin = AdvanceTo(new DateTimeOffset(2027, 3, 10, 9, 0, 0, TimeSpan.Zero));
        var created = await PostGroupAsync(NewGroup("Yeni Grup", capacity: 8), admin);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        await RecordOkAsync(e3, "2027-03", "500.00", admin, "2027-03-10");
        var cancelled = await RecordOkAsync(e2, "2027-03", "1000.00", admin, "2027-03-10");
        (await CancelAsync(IdOf(cancelled), "Yanlış kayıt", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SaveAttendanceAsync(admin, g1, "2027-03-03T17:00", (s1, "Present"), (s2, "Absent"), (s6, "Absent"));
        await SaveAttendanceAsync(admin, g2, "2027-03-10T10:00", (s3, "Present"));

        _s = new Seeded(g1, g2, g3, old, IdOf(await BodyOf(created)), swimming);
        return admin;
    }

    private sealed record Seeded(Guid G1, Guid G2, Guid G3, Guid Old, Guid New, Guid Swimming);

    [Fact]
    public async Task Attendance_per_group_and_month_matches_the_records()
    {
        var admin = await SeedAsync();

        var report = await GetOkAsync(admin, $"/api/v1/reports/attendance?{Range}");

        report.GetProperty("by").GetString().ShouldBe("Group");
        report.GetProperty("periods").EnumerateArray().Select(p => p.GetString()).ShouldBe(["2027-01", "2027-02", "2027-03"]);
        AttendanceRows(report).ShouldBe(
        [
            $"{_s.G1} U10 Futbol | 2027-01 2/0/1/0 0.6667 | 2027-02 1/1/0/1 1 | 2027-03 1/0/2/0 0.3333 | 4/1/3/1 0.625",
            $"{_s.G2} Yüzme | 2027-01 0/0/0/0 - | 2027-02 1/0/1/0 0.5 | 2027-03 1/0/0/0 1 | 2/0/1/0 0.6667",
        ]);
        AttendanceRow(report.GetProperty("total")).ShouldBe(
            " | 2027-01 2/0/1/0 0.6667 | 2027-02 2/1/1/1 0.75 | 2027-03 2/0/2/0 0.5 | 6/1/4/1 0.6364");
    }

    [Fact]
    public async Task Attendance_per_sport_and_coach_and_the_drill_down_to_groups()
    {
        var admin = await SeedAsync();

        var bySport = await GetOkAsync(admin, $"/api/v1/reports/attendance?{Range}&by=sport");
        var byCoach = await GetOkAsync(admin, $"/api/v1/reports/attendance?{Range}&by=coach");
        var footballGroups = await GetOkAsync(admin, $"/api/v1/reports/attendance?{Range}&by=group&sportId={Football}");
        var mehmetsGroups = await GetOkAsync(admin, $"/api/v1/reports/attendance?{Range}&by=group&coachId={Mehmet.Id.Value}");

        bySport.GetProperty("rows").EnumerateArray().Select(r => $"{r.GetProperty("id").GetGuid()} {Total(r)}")
            .ShouldBe([$"{Football} 4/1/3/1 0.625", $"{_s.Swimming} 2/0/1/0 0.6667"], ignoreOrder: true);
        bySport.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == _s.Swimming).GetProperty("name").GetString().ShouldBe("Yüzme");
        byCoach.GetProperty("rows").EnumerateArray().Select(r => $"{r.GetProperty("id").GetGuid()} {Total(r)}")
            .ShouldBe([$"{Ayse.Id.Value} 4/1/3/1 0.625", $"{Mehmet.Id.Value} 2/0/1/0 0.6667"], ignoreOrder: true);
        footballGroups.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([_s.G1]);
        footballGroups.GetProperty("sportId").GetGuid().ShouldBe(Football);
        Total(footballGroups.GetProperty("total")).ShouldBe("4/1/3/1 0.625");
        mehmetsGroups.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([_s.G2]);
    }

    [Fact]
    public async Task Occupancy_counts_month_end_enrollments_against_the_groups_open_that_day()
    {
        var admin = await SeedAsync();

        var report = await GetOkAsync(admin, $"/api/v1/reports/occupancy?{Range}");

        report.GetProperty("periods").EnumerateArray().Select(p => $"{p.GetProperty("period").GetString()} {p.GetProperty("referenceDate").GetString()}")
            .ShouldBe(["2027-01 2027-01-31", "2027-02 2027-02-28", "2027-03 2027-03-10"]);
        OccupancyRows(report).ShouldBe(
        [
            $"{_s.Old} Eski Grup | 1/10 0.1 | 0/0 - | 0/0 -",
            $"{_s.G1} U10 Futbol | 3/4 0.75 | 3/4 0.75 | 3/4 0.75",
            $"{_s.G3} U12 Futbol | 1/10 0.1 | 1/10 0.1 | 1/10 0.1",
            $"{_s.New} Yeni Grup | 0/0 - | 0/0 - | 0/8 0",
            $"{_s.G2} Yüzme | 2/2 1 | 1/2 0.5 | 1/2 0.5",
        ]);
        OccupancyRow(report.GetProperty("total")).ShouldBe(" | 7/26 0.2692 | 5/16 0.3125 | 5/24 0.2083");

        var bySport = await GetOkAsync(admin, $"/api/v1/reports/occupancy?{Range}&by=sport");
        OccupancyRows(bySport).ShouldBe(
        [
            $"{Football} Futbol | 5/24 0.2083 | 4/14 0.2857 | 4/22 0.1818",
            $"{_s.Swimming} Yüzme | 2/2 1 | 1/2 0.5 | 1/2 0.5",
        ]);
    }

    [Fact]
    public async Task Revenue_follows_the_fee_rules_and_the_frozen_closed_month_and_agrees_with_the_dues_list()
    {
        var admin = await SeedAsync();

        var report = await GetOkAsync(admin, $"/api/v1/reports/revenue?{Range}");

        report.GetProperty("today").GetString().ShouldBe("2027-03-10");
        report.GetProperty("dueDay").GetInt32().ShouldBe(10);
        // list / sibling / group change / accrued / collected for period / outstanding / collected in month / rate / charges / discounted
        report.GetProperty("periods").EnumerateArray().Select(Revenue).ShouldBe(
        [
            "2027-01 4800.00 80.00 0.00 4720.00 2620.00 2100.00 2220.00 0.5551 6 1",
            "2027-02 4800.00 0.00 0.00 4800.00 1000.00 3800.00 1400.00 0.2083 6 0",
            "2027-03 4300.00 0.00 0.00 4300.00 500.00 3800.00 500.00 0.1163 5 0",
        ]);
        Revenue(report.GetProperty("total")).ShouldBe("- 13900.00 80.00 0.00 13820.00 4120.00 9700.00 4120.00 0.2981 17 1");

        // Aged from the 10th: January 59 days, February 28 days, March due today (not overdue yet).
        report.GetProperty("overdue").EnumerateArray()
            .Select(b => $"{b.GetProperty("fromDays").GetInt32()}-{(b.GetProperty("toDays").ValueKind == JsonValueKind.Null ? "" : b.GetProperty("toDays").GetInt32().ToString(CultureInfo.InvariantCulture))} {b.GetProperty("dues").GetInt32()} {AmountOf(b.GetProperty("amount"))}")
            .ShouldBe(["1-30 5 3800.00", "31-60 3 2100.00", "61-90 0 0.00", "91- 0 0.00"]);
        report.GetProperty("overdueDues").GetInt32().ShouldBe(8);
        AmountOf(report.GetProperty("overdueAmount")).ShouldBe("5900.00");
        report.GetProperty("notYetDueDues").GetInt32().ShouldBe(4);
        AmountOf(report.GetProperty("notYetDueAmount")).ShouldBe("3800.00");

        // The outstanding amounts are exactly the dues list's remaining amounts.
        foreach (var period in report.GetProperty("periods").EnumerateArray())
        {
            var dues = await OutstandingAsync($"?period={period.GetProperty("period").GetString()}&pageSize=100", admin);
            Amount(period.GetProperty("outstanding")).ShouldBe(dues.GetProperty("items").EnumerateArray().Sum(i => Amount(i.GetProperty("remaining"))));
        }
    }

    [Fact]
    public async Task Csv_exports_are_utf8_with_bom_semicolon_separated_and_match_the_report()
    {
        var admin = await SeedAsync();

        var revenue = await admin.GetAsync(new Uri($"/api/v1/reports/revenue/csv?{Range}", UriKind.Relative), Ct);

        revenue.StatusCode.ShouldBe(HttpStatusCode.OK);
        revenue.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        revenue.Content.Headers.ContentType.CharSet.ShouldBe("utf-8");
        revenue.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        revenue.Content.Headers.ContentDisposition.FileName!.Trim('"').ShouldBe("gelir-raporu_2027-01_2027-03.csv");
        revenue.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var bytes = await revenue.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
        lines[0].ShouldStartWith("Ay;Liste tutarı (TL);Kardeş indirimi (TL);");
        lines[1].ShouldBe("2027-01;4800,00;80,00;0,00;4720,00;2620,00;2100,00;2220,00;55,5;6;1");
        lines[4].ShouldBe("Toplam;13900,00;80,00;0,00;13820,00;4120,00;9700,00;4120,00;29,8;17;1");
        lines.ShouldContain("31–60 gün;3;2100,00");
        lines.ShouldContain("91 gün ve üzeri;0;0,00");

        // DEC-413: in English the headers, separator and decimal mark follow Accept-Language.
        using (var english = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/reports/revenue/csv?{Range}", UriKind.Relative)))
        {
            english.Headers.AcceptLanguage.ParseAdd("en-GB");
            var response = await admin.SendAsync(english, Ct);
            response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("revenue-report_2027-01_2027-03.csv");
            var englishBytes = await response.Content.ReadAsByteArrayAsync(Ct);
            var englishLines = Encoding.UTF8.GetString(englishBytes, 3, englishBytes.Length - 3).Split("\r\n");
            englishLines[0].ShouldStartWith("Period,List fees (TRY),");
            englishLines[1].ShouldBe("2027-01,4800.00,80.00,0.00,4720.00,2620.00,2100.00,2220.00,55.5,6,1");
        }

        var attendance = await ReadCsvAsync(admin, $"/api/v1/reports/attendance/csv?{Range}&by=coach");
        attendance[0].ShouldBe("Antrenör;Ay;Geldi;Geç;Gelmedi;Mazeretli;Devam oranı (%)");
        attendance.ShouldContain("Tüm okul;Toplam;6;1;4;1;63,6");
        attendance.ShouldContain("Tüm okul;2027-01;2;0;1;0;66,7");

        var occupancy = await ReadCsvAsync(admin, $"/api/v1/reports/occupancy/csv?{Range}&by=sport");
        occupancy[0].ShouldBe("Branş;Ay;Sayım günü;Kayıtlı;Kapasite;Doluluk (%)");
        occupancy.ShouldContain("Yüzme;2027-03;10.03.2027;1;2;50,0");
        occupancy.ShouldContain("Tüm okul;2027-02;28.02.2027;5;16;31,3");
        // Aggregates only: no student name appears in any export.
        foreach (var name in new[] { "Ali", "Veli", "Zeynep", "Mert", "Ece" })
        {
            attendance.Concat(occupancy).Concat(lines).ShouldNotContain(line => line.Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_csv_cell_can_never_start_a_formula()
    {
        var group = IdOf(await GroupAsync("=HYPERLINK(\"x\";\"y\")", 5, Ayse, Slot("Wednesday", "17:00", "18:00")));
        var student = await RegisterStudentAsync("Ali");
        await EnrollOkAsync(student, group, "2027-01-04");
        await SaveAttendanceAsync(Admin, group, "2027-01-13T17:00", (student, "Present"));

        var lines = await ReadCsvAsync(Admin, "/api/v1/reports/attendance/csv");

        lines.ShouldContain("\"'=HYPERLINK(\"\"x\"\";\"\"y\"\")\";2027-01;1;0;0;0;100,0");
        lines.Skip(1).ShouldAllBe(line => line.Length == 0 || !"=+-@".Contains(line[0], StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_shows_up_in_the_next_report_at_once()
    {
        var group = IdOf(await GroupAsync("U10 Futbol", 5, Ayse, Slot("Wednesday", "17:00", "18:00")));
        var student = await RegisterStudentAsync("Ali");
        await EnrollOkAsync(student, group, "2027-01-04");
        Total((await GetOkAsync(Admin, "/api/v1/reports/attendance")).GetProperty("total")).ShouldBe("0/0/0/0 -");

        await SaveAttendanceAsync(Admin, group, "2027-01-13T17:00", (student, "Absent"));

        Total((await GetOkAsync(Admin, "/api/v1/reports/attendance")).GetProperty("total")).ShouldBe("0/0/1/0 0");
    }

    [Fact]
    public async Task The_default_range_is_the_last_six_months_and_bad_ranges_are_refused()
    {
        var report = await GetOkAsync(Admin, "/api/v1/reports/revenue");
        report.GetProperty("from").GetString().ShouldBe("2026-08");
        report.GetProperty("to").GetString().ShouldBe("2027-01");

        foreach (var (query, code) in new[]
        {
            ("from=2026-01&to=2027-01", "Report.InvalidRange"), // 13 months
            ("from=2027-01&to=2027-02", "Report.InvalidRange"), // after the current month
            ("from=2024-12&to=2025-01", "Report.InvalidRange"), // before the 24-month window
            ("from=2027-01&to=2026-12", "Report.InvalidRange"),
            ("from=2027-13", "YearMonth.Invalid"),
        })
        {
            foreach (var report2 in new[] { "attendance", "occupancy", "revenue", "revenue/csv" })
            {
                var response = await Admin.GetAsync(new Uri($"/api/v1/reports/{report2}?{query}", UriKind.Relative), Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{report2}?{query}");
                (await BodyOf(response)).ToString().ShouldContain(code);
            }
        }

        var by = await Admin.GetAsync(new Uri("/api/v1/reports/attendance?by=student", UriKind.Relative), Ct);
        by.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(by)).ToString().ShouldContain("Field.Invalid");
    }

    [Fact]
    public async Task Coaches_cannot_read_reports_and_exports_are_rate_limited()
    {
        using var coach = CoachClient(Ayse);
        foreach (var url in new[] { "attendance", "occupancy", "revenue", "attendance/csv", "occupancy/csv", "revenue/csv" })
        {
            (await coach.GetAsync(new Uri($"/api/v1/reports/{url}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await Admin.GetAsync(new Uri("/api/v1/reports/occupancy/csv", UriKind.Relative), Ct)).StatusCode);
        }

        statuses.Take(10).ShouldAllBe(s => s == HttpStatusCode.OK);
        statuses[10].ShouldBe(HttpStatusCode.TooManyRequests);
        // The page's JSON reads are not part of the export budget.
        (await Admin.GetAsync(new Uri("/api/v1/reports/occupancy", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Story", "US-02")]
    public async Task Another_schools_sport_or_coach_in_the_filters_is_not_found_and_its_reports_hold_nothing_of_this_school()
    {
        var admin = await SeedAsync();
        var other = Api.SchoolAdminClient(await TestSchools.CreateAsync(Api, "okul-b"));

        foreach (var (url, code) in new[]
        {
            ($"/api/v1/reports/attendance?{Range}&by=group&sportId={Football}", "Sport.NotFound"),
            ($"/api/v1/reports/occupancy?{Range}&by=group&coachId={Ayse.Id.Value}", "User.NotFound"),
            ($"/api/v1/reports/attendance/csv?{Range}&coachId={Mehmet.Id.Value}", "User.NotFound"),
            ($"/api/v1/reports/occupancy/csv?{Range}&sportId={_s.Swimming}", "Sport.NotFound"),
        })
        {
            var response = await other.GetAsync(new Uri(url, UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
            (await CodeOf(response)).ShouldBe(code, url);
        }

        foreach (var url in new[] { $"/api/v1/reports/attendance?{Range}", $"/api/v1/reports/occupancy?{Range}", $"/api/v1/reports/revenue?{Range}" })
        {
            var body = await GetOkAsync(other, url);
            body.ToString().ShouldNotContain(_s.G1.ToString());
            if (body.TryGetProperty("rows", out var rows))
            {
                rows.GetArrayLength().ShouldBe(0, url);
            }
            else
            {
                AmountOf(body.GetProperty("total").GetProperty("accrued")).ShouldBe("0.00");
            }
        }

        (await GetOkAsync(admin, $"/api/v1/reports/attendance?{Range}")).GetProperty("rows").GetArrayLength().ShouldBe(2);
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
        return await BodyOf(response);
    }

    private static async Task<string[]> ReadCsvAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
    }

    private static async Task SaveAttendanceAsync(HttpClient client, Guid group, string key, params (Guid Student, string Status)[] marks)
    {
        var body = new { records = marks.Select(m => new { studentId = m.Student, status = m.Status }).ToArray() };
        (await client.PutAsJsonAsync($"/api/v1/groups/{group}/sessions/{key}/attendance", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<int> RunCloseJobAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BillingPeriodCloseJob>().RunAsync(Ct);
    }

    private object NewGuardianLink(string firstName) =>
        new
        {
            newGuardian = new { firstName, lastName = "Kardeş", phone = string.Create(CultureInfo.InvariantCulture, $"0535 {Interlocked.Increment(ref _phones):000} 00 00"), email = "kardes@example.com" },
            relationship = "Mother",
        };

    private async Task<(Guid Student, Guid[] Guardians)> RegisterWithGuardiansAsync(string firstName, params object[] guardians)
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", new
        {
            firstName,
            lastName = "Kardeş",
            birthDate = "2015-05-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = string.Create(CultureInfo.InvariantCulture, $"0536 {Interlocked.Increment(ref _phones):000} 00 00"), relationship = "Teyze" },
            guardians,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyOf(response);
        return (IdOf(body), [.. body.GetProperty("guardians").EnumerateArray().Select(g => g.GetProperty("guardianId").GetGuid())]);
    }

    private static async Task ReplaceGuardiansAsync(Guid student, HttpClient client, params Guid[] guardians)
    {
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/students/{student}/guardians",
            new { guardians = guardians.Select(id => new { guardianId = id, relationship = "Father" }).ToArray(), version = current.GetProperty("version").GetUInt32() },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static decimal Amount(JsonElement money) => decimal.Parse(AmountOf(money), CultureInfo.InvariantCulture);

    private static string Rate(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? "-" : value.GetDecimal().ToString(CultureInfo.InvariantCulture);

    private static string Counts(JsonElement cell) =>
        $"{cell.GetProperty("present").GetInt32()}/{cell.GetProperty("late").GetInt32()}/{cell.GetProperty("absent").GetInt32()}/{cell.GetProperty("excused").GetInt32()} {Rate(cell.GetProperty("rate"))}";

    private static string Total(JsonElement row) => Counts(row.GetProperty("total"));

    private static string AttendanceRow(JsonElement row) =>
        $"{(row.GetProperty("id").ValueKind == JsonValueKind.Null ? "" : row.GetProperty("id").GetGuid().ToString())}{(row.GetProperty("name").GetString() is { Length: > 0 } name ? " " + name : "")} | "
        + string.Join(" | ", row.GetProperty("months").EnumerateArray().Select(m => $"{m.GetProperty("period").GetString()} {Counts(m)}"))
        + $" | {Total(row)}";

    private static List<string> AttendanceRows(JsonElement report) => [.. report.GetProperty("rows").EnumerateArray().Select(AttendanceRow)];

    private static string OccupancyRow(JsonElement row) =>
        $"{(row.GetProperty("id").ValueKind == JsonValueKind.Null ? "" : row.GetProperty("id").GetGuid().ToString())}{(row.GetProperty("name").GetString() is { Length: > 0 } name ? " " + name : "")} | "
        + string.Join(" | ", row.GetProperty("months").EnumerateArray().Select(m => $"{m.GetProperty("enrolled").GetInt32()}/{m.GetProperty("capacity").GetInt32()} {Rate(m.GetProperty("utilisation"))}"));

    private static List<string> OccupancyRows(JsonElement report) => [.. report.GetProperty("rows").EnumerateArray().Select(OccupancyRow)];

    private static string Revenue(JsonElement p) => string.Join(' ', [
        p.GetProperty("period").ValueKind == JsonValueKind.Null ? "-" : p.GetProperty("period").GetString()!,
        AmountOf(p.GetProperty("listFees")),
        AmountOf(p.GetProperty("siblingDiscount")),
        AmountOf(p.GetProperty("groupChangeWaived")),
        AmountOf(p.GetProperty("accrued")),
        AmountOf(p.GetProperty("collectedForPeriod")),
        AmountOf(p.GetProperty("outstanding")),
        AmountOf(p.GetProperty("collectedInMonth")),
        Rate(p.GetProperty("collectionRate")),
        p.GetProperty("charges").GetInt32().ToString(CultureInfo.InvariantCulture),
        p.GetProperty("siblingDiscountedCharges").GetInt32().ToString(CultureInfo.InvariantCulture),
    ]);
}
