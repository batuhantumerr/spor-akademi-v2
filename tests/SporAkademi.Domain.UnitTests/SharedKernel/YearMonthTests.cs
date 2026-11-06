using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class YearMonthTests
{
    [Theory]
    [InlineData(2000, 1)]
    [InlineData(2100, 12)]
    [InlineData(2027, 6)]
    public void Valid_values_are_accepted(int year, int month)
    {
        YearMonth.Create(year, month).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1999, 12)]
    [InlineData(2101, 1)]
    [InlineData(2027, 0)]
    [InlineData(2027, 13)]
    public void Invalid_values_are_rejected(int year, int month)
    {
        YearMonth.Create(year, month).Error.ShouldBe(YearMonth.Invalid);
    }

    [Theory]
    [InlineData("2027-01", 2027, 1)]
    [InlineData("2027-12", 2027, 12)]
    public void Parses_iso_text(string text, int year, int month)
    {
        YearMonth.Parse(text).Value.ShouldBe(YearMonth.Create(year, month).Value);
    }

    [Theory]
    [InlineData("2027-1")]
    [InlineData("2027/01")]
    [InlineData("27-01")]
    [InlineData("2027-13")]
    [InlineData("")]
    public void Rejects_malformed_text(string text)
    {
        YearMonth.Parse(text).Error.ShouldBe(YearMonth.Invalid);
    }

    [Fact]
    public void Is_comparable_and_formats_as_iso()
    {
        var jan = YearMonth.Create(2027, 1).Value;
        var dec = YearMonth.Create(2026, 12).Value;

        (dec < jan).ShouldBeTrue();
        dec.Next().ShouldBe(jan);
        jan.ToString().ShouldBe("2027-01");
    }

    [Fact]
    public void Knows_its_first_and_last_day()
    {
        var feb = YearMonth.Create(2028, 2).Value;

        feb.FirstDay.ShouldBe(new DateOnly(2028, 2, 1));
        feb.LastDay.ShouldBe(new DateOnly(2028, 2, 29));
        YearMonth.From(new DateOnly(2028, 2, 14)).ShouldBe(feb);
    }
}
