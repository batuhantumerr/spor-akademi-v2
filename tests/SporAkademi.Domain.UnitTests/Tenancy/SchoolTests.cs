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
    [InlineData(5, 10)]
    [InlineData(121, 10)]
    [InlineData(24, 51)]
    public void Retention_periods_must_be_within_bounds(int months, int years)
    {
        RetentionPolicy.Create(months, years).Error.ShouldBe(RetentionPolicy.Invalid);
    }

    [Theory]
    [Trait("Story", "P2-F5")]
    [InlineData("2027-01-14", 24, "2025-01-14")]
    [InlineData("2027-08-31", 6, "2027-02-28")] // clamped to the month's end, so the period never ends early
    [InlineData("2028-08-31", 6, "2028-02-29")]
    public void The_anonymization_cutoff_is_today_minus_the_retention_months(string today, int months, string cutoff)
    {
        var policy = RetentionPolicy.Create(months, 10).Value;

        policy.AnonymizationCutoff(DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(cutoff, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Story", "P2-F5")]
    public void Changing_the_personal_data_period_keeps_the_payment_period()
    {
        var school = School.Create("Okul", ValidSlug, "Europe/Istanbul", RetentionPolicy.Default).Value;

        school.ChangePersonalDataRetention(36).IsSuccess.ShouldBeTrue();
        school.RetentionPolicy.ShouldBe(RetentionPolicy.Create(36, 10).Value);
        school.ChangePersonalDataRetention(3).Error.ShouldBe(RetentionPolicy.Invalid);
        school.RetentionPolicy.PersonalDataMonths.ShouldBe(36);
    }
}
