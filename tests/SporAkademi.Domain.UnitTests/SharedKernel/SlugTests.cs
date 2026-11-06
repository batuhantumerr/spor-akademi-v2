using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.UnitTests.SharedKernel;

public sealed class SlugTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("kadikoy-spor")]
    [InlineData("a1-b2-c3")]
    [InlineData("12345678901234567890123456789012345678901234567890")] // 50
    public void Valid_slugs_are_accepted(string input)
    {
        Slug.Create(input).Value.Value.ShouldBe(input);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("123456789012345678901234567890123456789012345678901")] // 51
    [InlineData("Kadikoy")]
    [InlineData("kadıköy")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("ab--c")]
    [InlineData("ab_c")]
    [InlineData("abc\n")]
    public void Invalid_slugs_are_rejected(string input)
    {
        Slug.Create(input).Error.Code.ShouldBe("Slug.Invalid");
    }

    [Fact]
    public void Blank_is_required()
    {
        Slug.Create("").Error.Code.ShouldBe("Field.Required");
        Slug.Create("   ").Error.Code.ShouldBe("Field.Required");
    }
}
