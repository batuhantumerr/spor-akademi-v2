using System.Globalization;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>A half-open time-of-day interval <c>[Start, End)</c>: 10–11 and 11–12 do not overlap.</summary>
public sealed record TimeRange
{
    public static readonly Error Invalid = Error.Validation("TimeRange.Invalid", "End must be after start.");

    private TimeRange(TimeOnly start, TimeOnly end)
    {
        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public TimeSpan Duration => End - Start;

    public static Result<TimeRange> Create(TimeOnly start, TimeOnly end) =>
        end > start ? Result.Success(new TimeRange(start, end)) : Result.Failure<TimeRange>(Invalid);

    public bool Overlaps(TimeRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.End && other.Start < End;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Start:HH:mm}-{End:HH:mm}");
}
