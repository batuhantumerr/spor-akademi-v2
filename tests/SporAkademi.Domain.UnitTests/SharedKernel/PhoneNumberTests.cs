using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("05321234567")]
    [InlineData("5321234567")]
    [InlineData("+905321234567")]
    [InlineData("905321234567")]
    [InlineData("0 (532) 123 45 67")]
    [InlineData("0532-123-45-67")]
    [InlineData("+90 532 123 45 67")]
    [InlineData("0532.123.45.67")]
    public void Common_formats_normalize_to_e164(string input)
    {
        PhoneNumber.Create(input).Value.Value.ShouldBe("+905321234567");
    }

    [Theory]
    [InlineData("02121234567", "+902121234567")]
    [InlineData("03121234567", "+903121234567")]
    [InlineData("04121234567", "+904121234567")]
    public void Landlines_are_valid(string input, string expected)
    {
        PhoneNumber.Create(input).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("01234567890")] // first digit 1
    [InlineData("06321234567")] // first digit 6
    [InlineData("0532123456")] // too short
    [InlineData("053212345678")] // too long
    [InlineData("+15321234567")] // not Turkey
    [InlineData("0532abc4567")]
    [InlineData("+90 532 123 45 67 8")]
    [InlineData("+05321234567")] // plus with a trunk prefix
    [InlineData("0532+1234567")]
    [InlineData("07321234567")]
    [InlineData("09321234567")]
    public void Invalid_numbers_are_rejected(string input)
    {
        PhoneNumber.Create(input).Error.Code.ShouldBe("PhoneNumber.Invalid");
    }

    [Fact]
    public void Blank_is_required()
    {
        PhoneNumber.Create(" ").Error.Code.ShouldBe("Field.Required");
    }

    [Fact]
    public void Null_is_a_programmer_error()
    {
        Should.Throw<ArgumentNullException>(() => PhoneNumber.Create(null!));
    }
}
