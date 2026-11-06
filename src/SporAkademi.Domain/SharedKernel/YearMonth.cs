using System.Globalization;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>A calendar month used as a billing period, e.g. <c>2027-01</c>.</summary>
public sealed record YearMonth : IComparable<YearMonth>
{
    public const int MinYear = 2000;
    public const int MaxYear = 2100;

    public static readonly Error Invalid = Error.Validation("YearMonth.Invalid", "The period is not valid.");

    private YearMonth(int year, int month)
    {
        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    public DateOnly FirstDay => new(Year, Month, 1);

    public DateOnly LastDay => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    public static Result<YearMonth> Create(int year, int month) =>
        year is >= MinYear and <= MaxYear && month is >= 1 and <= 12
            ? Result.Success(new YearMonth(year, month))
            : Result.Failure<YearMonth>(Invalid);

    public static YearMonth From(DateOnly date)
    {
        var result = Create(date.Year, date.Month);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentOutOfRangeException(nameof(date), date, "Date is outside the supported period range.");
    }

    /// <summary>Parses exactly <c>YYYY-MM</c>.</summary>
    public static Result<YearMonth> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length != 7 || text[4] != '-'
            || !int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(text.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month))
        {
            return Result.Failure<YearMonth>(Invalid);
        }

        return Create(year, month);
    }

    /// <summary>The following month. Going past <see cref="MaxYear"/> is a programmer error.</summary>
    public YearMonth Next()
    {
        var next = Month == 12 ? Create(Year + 1, 1) : Create(Year, Month + 1);
        return next.IsSuccess ? next.Value : throw new InvalidOperationException("No period after the supported range.");
    }

    public int CompareTo(YearMonth? other) =>
        other is null ? 1 : ((Year * 12) + Month).CompareTo((other.Year * 12) + other.Month);

    public static bool operator <(YearMonth left, YearMonth right) => Compare(left, right) < 0;

    public static bool operator >(YearMonth left, YearMonth right) => Compare(left, right) > 0;

    public static bool operator <=(YearMonth left, YearMonth right) => Compare(left, right) <= 0;

    public static bool operator >=(YearMonth left, YearMonth right) => Compare(left, right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");

    private static int Compare(YearMonth left, YearMonth right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }
}
