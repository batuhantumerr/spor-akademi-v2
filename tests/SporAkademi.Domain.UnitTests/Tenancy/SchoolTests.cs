using Microsoft.Extensions.Time.Testing;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Tenancy;

[Trait("Story", "US-01")]
public sealed class SchoolTests
{
    private static readonly Slug ValidSlug = Slug.Create("kadikoy-spor").Value;

    [Fact]
    public void New_school_is_active()
    {
        var school = School.Create("  Kadıköy Spor  ", ValidSlug, "Europe/Istanbul", RetentionPolicy.Default).Value;

        school.Name.ShouldBe("Kadıköy Spor");
        school.Status.ShouldBe(SchoolStatus.Active);
        school.Id.Value.Version.ShouldBe(7);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("")]
    public void Unknown_time_zones_are_rejected(string timeZone)
    {
        School.Create("Okul", ValidSlug, timeZone, RetentionPolicy.Default).Error.ShouldBe(SchoolErrors.InvalidTimeZone);
    }

    [Fact]
    public void Name_is_required_and_bounded()
    {
        School.Create(" ", ValidSlug, "Europe/Istanbul", RetentionPolicy.Default).Error.Code.ShouldBe("Field.Required");
        School.Create(new string('a', 151), ValidSlug, "Europe/Istanbul", RetentionPolicy.Default).Error.Code.ShouldBe("Field.TooLong");
    }

    [Fact]
    public void Today_uses_the_school_time_zone()
    {
        // 21:30 UTC on 31 Dec is already 1 Jan in Istanbul (UTC+3).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 21, 30, 0, TimeSpan.Zero));
        var school = School.Create("Okul", ValidSlug, "Europe/Istanbul", RetentionPolicy.Default).Value;

        school.Today(clock).ShouldBe(new DateOnly(2027, 1, 1));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(24, 0)]
    [InlineData(-1, 10)]
    public void Retention_periods_must_be_positive(int months, int years)
    {
        RetentionPolicy.Create(months, years).Error.ShouldBe(RetentionPolicy.Invalid);
    }
}
