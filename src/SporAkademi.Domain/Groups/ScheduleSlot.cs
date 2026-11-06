using System.Globalization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Groups;

/// <summary>A weekly recurring training time of a group (Tuesday 17:00–18:30), in the school's time zone.</summary>
public sealed record ScheduleSlot
{
    private ScheduleSlot(DayOfWeek day, TimeRange time)
    {
        Day = day;
        Time = time;
    }

    // For EF Core materialization only (records have a copy constructor EF cannot bind).
    private ScheduleSlot()
    {
        Time = null!;
    }

    public DayOfWeek Day { get; private init; }

    public TimeRange Time { get; private init; }

    public static Result<ScheduleSlot> Create(DayOfWeek day, TimeRange time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return Enum.IsDefined(day)
            ? Result.Success(new ScheduleSlot(day, time))
            : Result.Failure<ScheduleSlot>(CommonErrors.FieldInvalid);
    }

    /// <summary>Same weekday and overlapping half-open times: 17:00–18:00 and 18:00–19:00 do not overlap.</summary>
    public bool Overlaps(ScheduleSlot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Day == other.Day && Time.Overlaps(other.Time);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Day} {Time}");
}
