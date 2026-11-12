using System.Text;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Reports;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.UnitTests.Reports;

/// <summary>DEC-411: CSV exports for Excel in Turkish, safe against formula (CSV) injection.</summary>
[Trait("Story", "P3-09")]
public sealed class ReportCsvTests
{
    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+90 532", "'+90 532")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tGrup", "'\tGrup")]
    [InlineData("\rGrup", "\"'\rGrup\"")]
    [InlineData("=HYPERLINK(\"http://x\";\"y\")", "\"'=HYPERLINK(\"\"http://x\"\";\"\"y\"\")\"")]
    public void A_cell_that_would_start_a_formula_is_prefixed_with_an_apostrophe(string value, string expected) =>
        ReportCsv.Escape(value).ShouldBe(expected);

    [Theory]
    [InlineData("U10 Futbol", "U10 Futbol")]
    [InlineData("Grup; A", "\"Grup; A\"")]
    [InlineData("\"Yıldızlar\"", "\"\"\"Yıldızlar\"\"\"")]
    [InlineData("İki\nsatır", "\"İki\nsatır\"")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Separators_quotes_and_line_breaks_are_quoted(string? value, string expected) =>
        ReportCsv.Escape(value).ShouldBe(expected);

    [Fact]
    public async Task Rows_are_written_with_semicolons_and_crlf()
    {
        var builder = new StringBuilder();
        await using var writer = new StringWriter(builder);

        await ReportCsv.Turkish.WriteAsync(writer, [["Grup", "Ay"], ["=Kötü;ad", "2027-01"], []], TestContext.Current.CancellationToken);

        builder.ToString().ShouldBe("Grup;Ay\r\n\"'=Kötü;ad\";2027-01\r\n\r\n");
    }

    [Fact]
    public void Revenue_rows_use_a_decimal_comma_and_never_group_thousands()
    {
        static MoneyDto M(string amount) => new(amount, "TRY");
        var period = new RevenuePeriodDto("2027-01", M("1234567.50"), M("80.00"), M("0.00"), M("1234487.50"), M("1000.00"), M("1233487.50"), M("1000.00"), 0.0008m, 3, 1);
        var report = new RevenueReportDto(
            "2027-01", "2027-01", new DateOnly(2027, 3, 10), 10, [period], period with { Period = null },
            [new OverdueBucketDto(1, 30, 1, M("5.00")), new OverdueBucketDto(91, null, 0, M("0.00"))], 1, M("5.00"), 0, M("0.00"));

        var rows = ReportCsv.Turkish.Revenue(report).Select(r => string.Join(';', r)).ToList();

        rows[1].ShouldBe("2027-01;1234567,50;80,00;0,00;1234487,50;1000,00;1233487,50;1000,00;0,1;3;1");
        rows[2].ShouldStartWith("Toplam;");
        rows.ShouldContain("1–30 gün;1;5,00");
        rows.ShouldContain("91 gün ve üzeri;0;0,00");
    }

    [Fact]
    public void A_coach_row_without_a_coach_is_named_and_the_school_total_comes_last()
    {
        var cell = new AttendanceCellDto("2027-01", 1, 0, 1, 0, 0.5m);
        var row = new AttendanceRowDto(null, string.Empty, [cell], cell with { Period = null });
        var report = new AttendanceReportDto("2027-01", "2027-01", ReportDimension.Coach, null, null, ["2027-01"], [row], row);

        var rows = ReportCsv.Turkish.Attendance(report).Select(r => string.Join(';', r)).ToList();

        rows.ShouldBe(
        [
            "Antrenör;Ay;Geldi;Geç;Gelmedi;Mazeretli;Devam oranı (%)",
            "Antrenör atanmamış;2027-01;1;0;1;0;50,0",
            "Antrenör atanmamış;Toplam;1;0;1;0;50,0",
            "Tüm okul;2027-01;1;0;1;0;50,0",
            "Tüm okul;Toplam;1;0;1;0;50,0",
        ]);
    }

    [Fact]
    public async Task English_uses_commas_a_decimal_point_and_english_headers()
    {
        static MoneyDto M(string amount) => new(amount, "TRY");
        var period = new RevenuePeriodDto("2027-01", M("1234.50"), M("80.00"), M("0.00"), M("1154.50"), M("1000.00"), M("154.50"), M("1000.00"), 0.8662m, 2, 1);
        var report = new RevenueReportDto(
            "2027-01", "2027-01", new DateOnly(2027, 3, 10), 10, [period], period with { Period = null },
            [new OverdueBucketDto(31, 60, 1, M("154.50"))], 1, M("154.50"), 0, M("0.00"));
        var builder = new StringBuilder();
        await using var writer = new StringWriter(builder);

        await ReportCsv.English.WriteAsync(writer, ReportCsv.English.Revenue(report), TestContext.Current.CancellationToken);
        var lines = builder.ToString().Split("\r\n");

        lines[0].ShouldStartWith("Period,List fees (TRY),Sibling discount (TRY),");
        lines[1].ShouldBe("2027-01,1234.50,80.00,0.00,1154.50,1000.00,154.50,1000.00,86.6,2,1");
        lines[4].ShouldBe("Overdue fees (as of 10/03/2027; fees are due on day 10 of each month),Unpaid fees,Amount (TRY)");
        lines[5].ShouldBe("31–60 days,1,154.50");
        ReportCsv.English.FileName("revenue", "2027-01", "2027-03").ShouldBe("revenue-report_2027-01_2027-03.csv");
        ReportCsv.For("en").ShouldBeSameAs(ReportCsv.English);
        ReportCsv.For("tr").ShouldBeSameAs(ReportCsv.Turkish);
        ReportCsv.For(null).ShouldBeSameAs(ReportCsv.Turkish);
    }
}

/// <summary>DEC-406: a report covers at most 12 months, within the last 24, not after the current month.</summary>
[Trait("Story", "P3-09")]
public sealed class ReportRangeTests
{
    private static readonly YearMonth March2027 = YearMonth.Create(2027, 3).Value;

    [Theory]
    [InlineData(null, null, "2026-10", "2027-03")]
    [InlineData(null, "2026-01", "2025-08", "2026-01")]
    [InlineData(null, "2025-05", "2025-04", "2025-05")] // the default start stops at the 24-month horizon
    [InlineData("2026-04", "2027-03", "2026-04", "2027-03")]
    [InlineData("2027-03", null, "2027-03", "2027-03")]
    public void Valid_ranges_resolve(string? from, string? to, string expectedFrom, string expectedTo)
    {
        var range = ReportRange.Resolve(from, to, March2027);

        range.IsSuccess.ShouldBeTrue();
        range.Value.From.ToString().ShouldBe(expectedFrom);
        range.Value.To.ToString().ShouldBe(expectedTo);
    }

    [Theory]
    [InlineData("2026-03", "2027-03")] // 13 months
    [InlineData("2027-03", "2027-04")] // after the current month
    [InlineData("2025-03", "2025-04")] // before the horizon (2025-04)
    [InlineData("2027-02", "2027-01")]
    public void Invalid_ranges_are_refused(string from, string to) =>
        ReportRange.Resolve(from, to, March2027).Error.ShouldBe(ReportErrors.InvalidRange);

    [Fact]
    public void The_report_horizon_is_the_dues_horizon_so_no_valid_range_outruns_the_dues_summary()
    {
        // The revenue report reads the dues summary, which throws for a longer window (a 500): one source, one value.
        ReportRange.HorizonMonths.ShouldBe(SporAkademi.Application.Billing.OutstandingDues.HorizonMonths);

        var oldest = YearMonth.From(March2027.FirstDay.AddMonths(1 - ReportRange.HorizonMonths));
        ReportRange.Resolve(oldest.ToString(), oldest.ToString(), March2027).IsSuccess.ShouldBeTrue();
        var beyond = YearMonth.From(oldest.FirstDay.AddMonths(-1)).ToString();
        ReportRange.Resolve(beyond, beyond, March2027).Error.ShouldBe(ReportErrors.InvalidRange);
    }

    [Fact]
    public void Head_counts_are_taken_at_month_end_and_today_in_the_current_month()
    {
        var today = new DateOnly(2027, 3, 10);

        ReportRange.ReferenceDate(YearMonth.Create(2027, 2).Value, today).ShouldBe(new DateOnly(2027, 2, 28));
        ReportRange.ReferenceDate(March2027, today).ShouldBe(today);
    }
}
