using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.UnitTests.Billing;

[Trait("Story", "US-23")]
public sealed class MoneyDtoTests
{
    [Theory]
    [InlineData("1250", 1250)]
    [InlineData("1250.5", 1250.5)]
    [InlineData("0.10", 0.1)]
    [InlineData("9999999999.99", 9999999999.99)]
    public void Valid_text_amounts_round_trip_with_two_decimals(string text, decimal expected)
    {
        var money = new MoneyDto(text, "TRY").ToMoney().Value;

        money.Amount.ShouldBe(expected);
        var dto = MoneyDto.From(money);
        dto.Currency.ShouldBe("TRY");
        dto.Amount.ShouldBe(expected.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        dto.ToMoney().Value.ShouldBe(money);
    }

    [Theory]
    [InlineData("1.234", "TRY")]
    [InlineData("1,50", "TRY")]
    [InlineData("-5", "TRY")]
    [InlineData("1e3", "TRY")]
    [InlineData(" 10", "TRY")]
    [InlineData("", "TRY")]
    [InlineData("10.00", "USD")]
    [InlineData("10000000000.00", "TRY")]
    [InlineData("١٢٣", "TRY")]
    [InlineData("١٢.٣٤", "TRY")]
    [InlineData("１０", "TRY")]
    public void Anything_else_is_invalid(string text, string currency)
    {
        new MoneyDto(text, currency).ToMoney().Error.ShouldBe(Money.Invalid);
    }

    [Fact]
    public void Amounts_are_never_rounded_through_a_double()
    {
        var sum = new MoneyDto("0.10", "TRY").ToMoney().Value.Add(new MoneyDto("0.20", "TRY").ToMoney().Value);

        MoneyDto.From(sum).Amount.ShouldBe("0.30");
    }
}
