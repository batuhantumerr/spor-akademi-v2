using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1250")]
    [InlineData("1250.5")]
    [InlineData("1250.50")]
    [InlineData("9999999999.99")]
    public void Valid_try_amounts_are_accepted(string amount)
    {
        var money = Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "TRY");

        money.IsSuccess.ShouldBeTrue();
        money.Value.Currency.ShouldBe("TRY");
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.001")]
    [InlineData("10000000000.00")]
    public void Invalid_amounts_are_rejected(string amount)
    {
        Money.Create(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "TRY").Error.Code.ShouldBe("Money.Invalid");
    }

    [Fact]
    public void Only_try_is_supported()
    {
        Money.Create(10m, "USD").Error.Code.ShouldBe("Money.Invalid");
    }

    [Fact]
    public void Add_sums_amounts()
    {
        var sum = Money.Try(100.50m).Add(Money.Try(49.50m));

        sum.ShouldBe(Money.Try(150m));
    }

    [Fact]
    public void Equality_ignores_trailing_zeros()
    {
        Money.Try(1250m).ShouldBe(Money.Try(1250.00m));
        Money.Try(1250m).GetHashCode().ShouldBe(Money.Try(1250.00m).GetHashCode());
    }

    [Fact]
    public void Amount_text_always_has_two_decimals()
    {
        Money.Try(1250m).ToAmountString().ShouldBe("1250.00");
        Money.Try(0.5m).ToAmountString().ShouldBe("0.50");
    }

    [Fact]
    public void Zero_is_zero_try()
    {
        Money.Zero.ShouldBe(Money.Try(0m));
    }

    [Fact]
    public void Comparison_works()
    {
        (Money.Try(10m) > Money.Try(9.99m)).ShouldBeTrue();
        (Money.Try(10m) <= Money.Try(10m)).ShouldBeTrue();
    }

    [Fact]
    public void Subtract_rejects_negative_results()
    {
        Money.Try(100m).Subtract(Money.Try(40m)).ShouldBe(Money.Try(60m));
        Should.Throw<InvalidOperationException>(() => Money.Try(10m).Subtract(Money.Try(11m)));
    }

    [Fact]
    public void Add_past_the_maximum_is_a_programmer_error()
    {
        Should.Throw<InvalidOperationException>(() => Money.Try(Money.MaxAmount).Add(Money.Try(0.01m)));
    }

    [Theory]
    [InlineData("try")]
    [InlineData("")]
    public void Currency_must_be_exactly_try(string currency)
    {
        Money.Create(1m, currency).Error.Code.ShouldBe("Money.Invalid");
    }

    [Fact]
    public void Null_currency_is_a_programmer_error()
    {
        Should.Throw<ArgumentNullException>(() => Money.Create(1m, null!));
    }
}
