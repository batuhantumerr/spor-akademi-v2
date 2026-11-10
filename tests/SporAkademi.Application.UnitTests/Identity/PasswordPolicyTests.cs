using SporAkademi.Application.Identity;

namespace SporAkademi.Application.UnitTests.Identity;

[Trait("Story", "US-05")]
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("kısa-şifre-1")]
    [InlineData("Yaz-Kampı-2027")]
    [InlineData("correct horse battery staple")]
    public void Long_uncommon_passwords_are_accepted(string password)
    {
        PasswordPolicy.IsStrongEnough(password).ShouldBeTrue();
    }

    [Fact]
    public void The_minimum_length_is_twelve_characters()
    {
        PasswordPolicy.MinLength.ShouldBe(12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789")]
    [InlineData("Kisa-sifr")]
    [InlineData("kısa-şifre")]
    [InlineData("Kisa-sifre1")]
    public void Passwords_shorter_than_twelve_characters_are_rejected(string password)
    {
        PasswordPolicy.IsStrongEnough(password).ShouldBeFalse();
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("qwertyuiop")]
    [InlineData("QWERTYUIOP")]
    [InlineData("sifre12345")]
    [InlineData("Galatasaray1905")]
    [InlineData("password123")]
    public void Common_passwords_are_rejected_case_insensitively(string password)
    {
        PasswordPolicy.IsStrongEnough(password).ShouldBeFalse();
    }

    [Fact]
    public void Passwords_longer_than_the_maximum_are_rejected()
    {
        PasswordPolicy.IsStrongEnough(new string('x', PasswordPolicy.MaxLength) + "y").ShouldBeFalse();
    }

    [Fact]
    public void Temporary_passwords_are_random_and_satisfy_the_policy()
    {
        var first = TemporaryPassword.Generate();
        var second = TemporaryPassword.Generate();

        first.Length.ShouldBe(TemporaryPassword.Length);
        first.ShouldNotBe(second);
        PasswordPolicy.IsStrongEnough(first).ShouldBeTrue();
    }
}
