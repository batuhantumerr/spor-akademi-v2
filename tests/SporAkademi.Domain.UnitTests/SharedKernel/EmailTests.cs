using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class EmailTests
{
    [Theory]
    [InlineData("ali@example.com", "ali@example.com")]
    [InlineData("  Ali.Veli@Example.COM ", "ali.veli@example.com")]
    [InlineData("a+b@sub.example.org", "a+b@sub.example.org")]
    public void Valid_addresses_are_trimmed_and_lowercased(string input, string expected)
    {
        Email.Create(input).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("@example.com")]
    [InlineData("ali@")]
    [InlineData("ali@localhost")]
    [InlineData("ali@.example.com")]
    [InlineData("ali@example.com.")]
    [InlineData("ali veli@example.com")]
    public void Invalid_addresses_are_rejected(string input)
    {
        Email.Create(input).Error.Code.ShouldBe("Email.Invalid");
    }

    [Fact]
    public void Local_part_longer_than_64_is_rejected()
    {
        Email.Create(new string('a', 65) + "@example.com").Error.Code.ShouldBe("Email.Invalid");
        Email.Create(new string('a', 64) + "@example.com").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Address_longer_than_254_is_rejected()
    {
        var domain = string.Join('.', Enumerable.Repeat(new string('d', 60), 4)) + ".com"; // 247 chars
        ("abcdefg@" + domain).Length.ShouldBe(255);
        Email.Create("abcdefg@" + domain).Error.Code.ShouldBe("Email.Invalid");
        Email.Create("abcdef@" + domain).IsSuccess.ShouldBeTrue(); // 254
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_required(string input)
    {
        Email.Create(input).Error.Code.ShouldBe("Field.Required");
    }

    [Fact]
    public void Null_is_a_programmer_error()
    {
        Should.Throw<ArgumentNullException>(() => Email.Create(null!));
    }

    [Fact]
    public void Equal_values_are_equal()
    {
        Email.Create("A@x.com").Value.ShouldBe(Email.Create("a@x.com").Value);
    }
}
