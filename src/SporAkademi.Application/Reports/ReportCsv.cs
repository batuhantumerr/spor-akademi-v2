using System.Globalization;

namespace SporAkademi.Application.Reports;

/// <summary>
/// The CSV exports of the reports (P3-09, DEC-411), written for Excel in Turkish: UTF-8 with a byte order mark (the
/// caller's encoding), <c>;</c> between cells, CRLF line ends, Turkish headers, numbers with a decimal comma and no
/// grouping, months as <c>YYYY-MM</c> (sortable) and dates as <c>dd.MM.yyyy</c>. Aggregates only: no student appears.
/// Every cell passes <see cref="Escape"/>, so no cell can start a formula (CSV injection).
/// </summary>
public static class ReportCsv
{
    public const char Separator = ';';

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>The characters that make a spreadsheet read a cell as a formula (OWASP CSV injection).</summary>
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];

    private const string NoCoach = "Antrenör atanmamış";
    private const string TotalLabel = "Toplam";
    private const string SchoolLabel = "Tüm okul";

    /// <summary>
    /// One cell as written: a cell starting with <c>= + - @</c>, tab or CR gets a leading apostrophe (shown as text, never
    /// evaluated); a cell holding the separator, a quote, CR or LF is quoted with its quotes doubled.
    /// </summary>
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && Array.IndexOf(FormulaStarts, text[0]) >= 0)
        {
            text = "'" + text;
        }

        return text.IndexOfAny([Separator, '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    /// <summary>Writes the rows, one line each; nothing is buffered beyond the writer's own buffer.</summary>
    public static async Task WriteAsync(TextWriter writer, IEnumerable<IReadOnlyList<string>> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(string.Join(Separator, row.Select(Escape)));
            await writer.WriteAsync("\r\n");
        }

        await writer.FlushAsync(cancellationToken);
    }

    public static IEnumerable<IReadOnlyList<string>> Attendance(AttendanceReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        yield return [DimensionHeader(report.By), "Ay", "Geldi", "Geç", "Gelmedi", "Mazeretli", "Devam oranı (%)"];
        foreach (var (row, name) in report.Rows.Select(r => (r, NameOf(r.Id, r.Name, report.By))).Append((report.Total, SchoolLabel)))
        {
            foreach (var cell in row.Months.Append(row.Total))
            {
                yield return
                [
                    name,
                    cell.Period ?? TotalLabel,
                    Number(cell.Present),
                    Number(cell.Late),
                    Number(cell.Absent),
                    Number(cell.Excused),
                    Percent(cell.Rate),
                ];
            }
        }
    }

    public static IEnumerable<IReadOnlyList<string>> Occupancy(OccupancyReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var dates = report.Periods.ToDictionary(p => p.Period, p => p.ReferenceDate);
        yield return [DimensionHeader(report.By), "Ay", "Sayım günü", "Kayıtlı", "Kapasite", "Doluluk (%)"];
        foreach (var (row, name) in report.Rows.Select(r => (r, NameOf(r.Id, r.Name, report.By))).Append((report.Total, SchoolLabel)))
        {
            foreach (var cell in row.Months)
            {
                yield return
                [
                    name,
                    cell.Period,
                    dates[cell.Period].ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    Number(cell.Enrolled),
                    Number(cell.Capacity),
                    Percent(cell.Utilisation),
                ];
            }
        }
    }

    public static IEnumerable<IReadOnlyList<string>> Revenue(RevenueReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);
        yield return
        [
            "Ay", "Liste tutarı (TL)", "Kardeş indirimi (TL)", "Grup değişikliğinde alınmayan (TL)", "Tahakkuk (TL)",
            "Döneme yapılan tahsilat (TL)", "Kalan borç (TL)", "Ay içinde tahsil edilen (TL)", "Tahsil oranı (%)",
            "Aidat sayısı", "Kardeş indirimli aidat sayısı",
        ];
        foreach (var period in report.Periods.Append(report.Total))
        {
            yield return
            [
                period.Period ?? TotalLabel,
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
        yield return [$"Gecikmiş borçlar ({report.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} itibarıyla, son ödeme günü: her ayın {Number(report.DueDay)}. günü)", "Borç sayısı", "Tutar (TL)"];
        foreach (var bucket in report.Overdue)
        {
            var days = bucket.ToDays is { } to ? $"{Number(bucket.FromDays)}–{Number(to)} gün" : $"{Number(bucket.FromDays)} gün ve üzeri";
            yield return [days, Number(bucket.Dues), Amount(bucket.Amount.Amount)];
        }

        yield return ["Gecikmiş toplam", Number(report.OverdueDues), Amount(report.OverdueAmount.Amount)];
        yield return ["Vadesi gelmemiş", Number(report.NotYetDueDues), Amount(report.NotYetDueAmount.Amount)];
    }

    private static string DimensionHeader(ReportDimension by) => by switch
    {
        ReportDimension.Group => "Grup",
        ReportDimension.Sport => "Branş",
        ReportDimension.Coach => "Antrenör",
        _ => throw new ArgumentOutOfRangeException(nameof(by), by, "Unknown report dimension."),
    };

    private static string NameOf(Guid? id, string name, ReportDimension by) =>
        by == ReportDimension.Coach && id is null ? NoCoach : name;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"1234,56": a decimal comma and no grouping, so Excel in Turkish reads it as a number.</summary>
    private static string Amount(string amount) =>
        decimal.Parse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture).ToString("0.00", Turkish);

    /// <summary>A 0–1 ratio as a percent with one decimal ("87,5"); empty when there is nothing to divide by.</summary>
    private static string Percent(decimal? ratio) =>
        ratio is { } r ? decimal.Round(r * 100, 1, MidpointRounding.AwayFromZero).ToString("0.0", Turkish) : string.Empty;
}
