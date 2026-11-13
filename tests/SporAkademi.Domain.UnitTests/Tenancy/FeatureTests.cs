using System.Text.RegularExpressions;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Tenancy;

[Trait("Story", "P4-10")]
public sealed partial class FeatureTests
{
    private static readonly SchoolId School = SchoolId.From(Guid.CreateVersion7());

    [Fact]
    public void Catalog_keys_are_unique_short_kebab_case_names()
    {
        var keys = FeatureCatalog.All.Select(f => f.Key).ToList();

        keys.ShouldBeUnique();
        keys.ShouldAllBe(key => KebabCase().IsMatch(key) && key.Length <= FeatureCatalog.KeyMaxLength);
        FeatureCatalog.All.ShouldAllBe(f => !string.IsNullOrWhiteSpace(f.Description));
    }

    [Fact]
    public void A_known_key_creates_the_schools_value()
    {
        var feature = SchoolFeature.Create(School, "reports", enabled: false).Value;

        feature.TenantId.ShouldBe(School);
        feature.Key.ShouldBe(FeatureCatalog.Reports.Key);
        feature.Enabled.ShouldBeFalse();
        feature.Id.Value.Version.ShouldBe(7);

        feature.Set(true);
        feature.Enabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Reports")]
    [InlineData("")]
    public void Only_catalog_keys_exist(string key)
    {
        SchoolFeature.Create(School, key, enabled: true).Error.ShouldBe(FeatureErrors.NotFound);
        FeatureCatalog.Find(key).ShouldBeNull();
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial Regex KebabCase();
}
