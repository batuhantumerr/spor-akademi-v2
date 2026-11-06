using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class TimeRangeTests
{
    private static TimeRange Range(string start, string end) =>
        TimeRange.Create(TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture)).Value;

    [Fact]
    public void End_must_be_after_start()
    {
        TimeRange.Create(new TimeOnly(10, 0), new TimeOnly(10, 0)).Error.Code.ShouldBe("TimeRange.Invalid");
        TimeRange.Create(new TimeOnly(11, 0), new TimeOnly(10, 0)).Error.Code.ShouldBe("TimeRange.Invalid");
    }

    [Theory]
    [InlineData("10:00", "11:00", "11:00", "12:00", false)] // adjacent, half-open
    [InlineData("10:00", "11:00", "10:30", "11:30", true)]
    [InlineData("10:00", "12:00", "10:30", "11:00", true)] // contained
    [InlineData("10:00", "11:00", "09:00", "10:00", false)]
    [InlineData("10:00", "11:00", "10:00", "11:00", true)]
    public void Overlap_is_half_open(string s1, string e1, string s2, string e2, bool expected)
    {
        Range(s1, e1).Overlaps(Range(s2, e2)).ShouldBe(expected);
        Range(s2, e2).Overlaps(Range(s1, e1)).ShouldBe(expected);
    }

    [Fact]
    public void Duration_is_end_minus_start()
    {
        Range("17:00", "18:30").Duration.ShouldBe(TimeSpan.FromMinutes(90));
    }
}
