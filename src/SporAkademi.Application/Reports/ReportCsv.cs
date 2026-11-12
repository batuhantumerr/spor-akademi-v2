using System.Globalization;

namespace SporAkademi.Application.Reports;

/// <summary>
/// The CSV exports of the reports (P3-09, DEC-411, DEC-413), written for Excel in the caller's language: UTF-8 with a byte
/// order mark (the caller's encoding), CRLF line ends, headers in the language, numbers without grouping and months as
/// <c>YYYY-MM</c> (sortable). Turkish (<see cref="Turkish"/>) uses <c>;</c> between cells, a decimal comma and dates as
/// <c>dd.MM.yyyy</c>, which is what Excel in Turkish reads; English (<see cref="English"/>, en-GB like the panel,
/// DEC-427) uses <c>,</c>, a decimal point and <c>dd/MM/yyyy</c>. Aggregates only: no student appears. Every cell passes
/// <see cref="Escape(string?, char)"/>, so no cell can start a formula (CSV injection).
/// </summary>
public sealed class ReportCsv
{
    /// <summary>The characters that make a spreadsheet read a cell as a formula (OWASP CSV injection).</summary>
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    public static readonly ReportCsv Turkish = new(';', CultureInfo.GetCultureInfo("tr-TR"), "dd.MM.yyyy", CsvTexts.Turkish);

    public static readonly ReportCsv English = new(',', CultureInfo.GetCultureInfo("en-GB"), "dd/MM/yyyy", CsvTexts.English);

    private readonly CultureInfo _culture;
    private readonly string _dateFormat;
    private readonly CsvTexts _t;

    private ReportCsv(char separator, CultureInfo culture, string dateFormat, CsvTexts texts)
    {
        Separator = separator;
        _culture = culture;
        _dateFormat = dateFormat;
        _t = texts;
    }

    public char Separator { get; }

    /// <summary>The export for a language code (<c>en</c>; anything else is Turkish, the default language).</summary>
    public static ReportCsv For(string? language) => language == "en" ? English : Turkish;

    /// <summary>
    /// One cell as written: a cell starting with <c>= + - @</c>, tab or CR gets a leading apostrophe (shown as text, never
    /// evaluated); a cell holding the separator, a quote, CR or LF is quoted with its quotes doubled.
    /// </summary>
    public static string Escape(string? value, char separator = ';')
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && Array.IndexOf(FormulaStarts, text[0]) >= 0)
        {
            text = "'" + text;
        }

        return text.IndexOfAny([separator, '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    /// <summary>A file name in ASCII: <c>gelir-raporu_2027-01_2027-03.csv</c> or <c>revenue-report_2027-01_2027-03.csv</c>.</summary>
    public string FileName(string report, string from, string to) => $"{_t.FileNames[report]}_{from}_{to}.csv";

    /// <summary>Writes the rows, one line each; nothing is buffered beyond the writer's own buffer.</summary>
    public async Task WriteAsync(TextWriter writer, IEnumerable<IReadOnlyList<string>> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(string.Join(Separator, row.Select(cell => Escape(cell, Separator))));
            await writer.WriteAsync("\r\n");
        }

        await writer.FlushAsync(cancellationToken);
    }

    public IEnumerable<IReadOnlyList<string>> Attendance(AttendanceReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        yield return [DimensionHeader(report.By), .. _t.AttendanceHeaders];
        foreach (var (row, name) in report.Rows.Select(r => (r, NameOf(r.Id, r.Name, report.By))).Append((report.Total, _t.School)))
        {
            foreach (var cell in row.Months.Append(row.Total))
            {
                yield return
                [
                    name,
                    cell.Period ?? _t.Total,
                    Number(cell.Present),
                    Number(cell.Late),
                    Number(cell.Absent),
                    Number(cell.Excused),
                    Percent(cell.Rate),
                ];
            }
        }
    }

    public IEnumerable<IReadOnlyList<string>> Occupancy(OccupancyReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var dates = report.Periods.ToDictionary(p => p.Period, p => p.ReferenceDate);
        yield return [DimensionHeader(report.By), .. _t.OccupancyHeaders];
        foreach (var (row, name) in report.Rows.Select(r => (r, NameOf(r.Id, r.Name, report.By))).Append((report.Total, _t.School)))
        {
            foreach (var cell in row.Months)
            {
                yield return
                [
                    name,
                    cell.Period,
                    Date(dates[cell.Period]),
                    Number(cell.Enrolled),
                    Number(cell.Capacity),
                    Percent(cell.Utilisation),
                ];
            }
        }
    }

    public IEnumerable<IReadOnlyList<string>> Revenue(RevenueReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        yield return _t.RevenueHeaders;
        foreach (var period in report.Periods.Append(report.Total))
        {
            yield return
            [
                period.Period ?? _t.Total,
                Amount(period.ListFees.Amount),
                Amount(period.SiblingDiscount.Amount),
                Amount(period.GroupChangeWaived.Amount),
                Amount(period.Accrued.Amount),
                Amount(period.CollectedForPeriod.Amount),
                Amount(period.Outstanding.Amount),
                Amount(period.CollectedInMonth.Amount),
                Percent(period.CollectionRate),
                Number(period.Charges),
                Number(period.SiblingDiscountedCharges),
            ];
        }

        yield return [];
        yield return [string.Format(_culture, _t.OverdueTitle, Date(report.Today), Number(report.DueDay)), .. _t.OverdueHeaders];
        foreach (var bucket in report.Overdue)
        {
            var days = bucket.ToDays is { } to
                ? string.Format(_culture, _t.DaysRange, Number(bucket.FromDays), Number(to))
                : string.Format(_culture, _t.DaysOpen, Number(bucket.FromDays));
            yield return [days, Number(bucket.Dues), Amount(bucket.Amount.Amount)];
        }

        yield return [_t.OverdueTotal, Number(report.OverdueDues), Amount(report.OverdueAmount.Amount)];
        yield return [_t.NotYetDue, Number(report.NotYetDueDues), Amount(report.NotYetDueAmount.Amount)];
    }

    private string DimensionHeader(ReportDimension by) => by switch
    {
        ReportDimension.Group => _t.Group,
        ReportDimension.Sport => _t.Sport,
        ReportDimension.Coach => _t.Coach,
        _ => throw new ArgumentOutOfRangeException(nameof(by), by, "Unknown report dimension."),
    };

    private string NameOf(Guid? id, string name, ReportDimension by) =>
        by == ReportDimension.Coach && id is null ? _t.NoCoach : name;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private string Date(DateOnly date) => date.ToString(_dateFormat, CultureInfo.InvariantCulture);

    /// <summary>"1234,56" / "1234.56": no grouping, so Excel in that language reads it as a number.</summary>
    private string Amount(string amount) =>
        decimal.Parse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture).ToString("0.00", _culture);

    /// <summary>A 0–1 ratio as a percent with one decimal ("87,5" / "87.5"); empty when there is nothing to divide by.</summary>
    private string Percent(decimal? ratio) =>
        ratio is { } r ? decimal.Round(r * 100, 1, MidpointRounding.AwayFromZero).ToString("0.0", _culture) : string.Empty;

    /// <summary>The words of one language; English follows the panel's glossary (DEC-427).</summary>
    private sealed record CsvTexts(
        string Group,
        string Sport,
        string Coach,
        string NoCoach,
        string Total,
        string School,
        string[] AttendanceHeaders,
        string[] OccupancyHeaders,
        string[] RevenueHeaders,
        string OverdueTitle,
        string[] OverdueHeaders,
        string DaysRange,
        string DaysOpen,
        string OverdueTotal,
        string NotYetDue,
        IReadOnlyDictionary<string, string> FileNames)
    {
        public static readonly CsvTexts Turkish = new(
            "Grup",
            "Branş",
            "Antrenör",
            "Antrenör atanmamış",
            "Toplam",
            "Tüm okul",
            ["Ay", "Geldi", "Geç", "Gelmedi", "Mazeretli", "Devam oranı (%)"],
            ["Ay", "Sayım günü", "Kayıtlı", "Kapasite", "Doluluk (%)"],
            [
                "Ay", "Liste tutarı (TL)", "Kardeş indirimi (TL)", "Grup değişikliğinde alınmayan (TL)", "Tahakkuk (TL)",
                "Döneme yapılan tahsilat (TL)", "Kalan borç (TL)", "Ay içinde tahsil edilen (TL)", "Tahsil oranı (%)",
                "Aidat sayısı", "Kardeş indirimli aidat sayısı",
            ],
            "Gecikmiş borçlar ({0} itibarıyla, son ödeme günü: her ayın {1}. günü)",
            ["Borç sayısı", "Tutar (TL)"],
            "{0}–{1} gün",
            "{0} gün ve üzeri",
            "Gecikmiş toplam",
            "Vadesi gelmemiş",
            new Dictionary<string, string> { ["attendance"] = "devam-raporu", ["occupancy"] = "doluluk-raporu", ["revenue"] = "gelir-raporu" });

        public static readonly CsvTexts English = new(
            "Group",
            "Sport",
            "Coach",
            "No coach assigned",
            "Total",
            "Whole school",
            ["Month", "Present", "Late", "Absent", "Excused", "Attendance rate (%)"],
            ["Month", "Count date", "Enrolled", "Capacity", "Occupancy (%)"],
            [
                "Period", "List fees (TRY)", "Sibling discount (TRY)", "Waived on group change (TRY)", "Billed (TRY)",
                "Collected for the period (TRY)", "Unpaid (TRY)", "Collected in the month (TRY)", "Collection rate (%)",
                "Monthly fees", "Fees with a sibling discount",
            ],
            "Overdue fees (as of {0}; fees are due on day {1} of each month)",
            ["Unpaid fees", "Amount (TRY)"],
            "{0}–{1} days",
            "{0} days or more",
            "Total overdue",
            "Not yet due",
            new Dictionary<string, string> { ["attendance"] = "attendance-report", ["occupancy"] = "occupancy-report", ["revenue"] = "revenue-report" });
    }
}
