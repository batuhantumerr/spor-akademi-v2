using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Organization;

public sealed class OrganizationTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly Address SomeAddress = Address.Create("Cad. 1", "Kadıköy", "İstanbul").Value;

    [Theory]
    [Trait("Story", "US-06")]
    [InlineData("  Futbol  ", "Futbol", "futbol")]
    [InlineData("Masa   Tenisi", "Masa Tenisi", "masa tenisi")]
    [InlineData("İZCİLİK", "İZCİLİK", "izcilik")]
    [InlineData("IRMAK", "IRMAK", "ırmak")]
    [InlineData("IŞIK", "IŞIK", "ışık")]
    [InlineData("Iğdır", "Iğdır", "ığdır")]
    public void Sport_names_are_tidied_and_keyed_with_turkish_casing(string input, string name, string key)
    {
        var sport = Sport.Create(School, input).Value;

        sport.Name.ShouldBe(name);
        sport.NameKey.ShouldBe(key);
        sport.TenantId.ShouldBe(School);
    }

    [Fact]
    [Trait("Story", "US-06")]
    public void Sport_name_is_required_and_bounded()
    {
        Sport.Create(School, " ").Error.ShouldBe(CommonErrors.FieldRequired);
        Sport.Create(School, new string('a', 101)).Error.ShouldBe(CommonErrors.FieldTooLong);
    }

    [Fact]
    [Trait("Story", "US-06")]
    public void Rename_and_archive_change_state()
    {
        var sport = Sport.Create(School, "Futbol").Value;

        sport.Rename("Futbol (U10)").IsSuccess.ShouldBeTrue();
        sport.Archive();

        sport.NameKey.ShouldBe("futbol (u10)");
        sport.IsArchived.ShouldBeTrue();
        sport.Rename("").IsFailure.ShouldBeTrue();
        sport.Name.ShouldBe("Futbol (U10)");
    }

    [Theory]
    [Trait("Story", "US-07")]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Facility_capacity_must_be_positive_and_sane(int capacity)
    {
        Facility.Create(School, "Saha", FacilityType.Field, SomeAddress, capacity).Error.ShouldBe(CommonErrors.FieldInvalid);
    }

    [Fact]
    [Trait("Story", "US-07")]
    public void Facility_without_capacity_is_valid()
    {
        var facility = Facility.Create(School, "Havuz", FacilityType.Pool, SomeAddress, null).Value;

        facility.Capacity.ShouldBeNull();
        facility.Type.ShouldBe(FacilityType.Pool);
    }

    [Fact]
    public void Undefined_facility_type_is_rejected()
    {
        Facility.Create(School, "Saha", (FacilityType)99, SomeAddress, null).Error.ShouldBe(CommonErrors.FieldInvalid);
    }

    [Fact]
    public void Address_parts_are_required()
    {
        Address.Create("", "Kadıköy", "İstanbul").Error.ShouldBe(CommonErrors.FieldRequired);
        Address.Create(new string('a', 201), "Kadıköy", "İstanbul").Error.ShouldBe(CommonErrors.FieldTooLong);
    }

    [Theory]
    [Trait("Story", "US-16")]
    [InlineData("Şükrü", "sukru")]
    [InlineData("ÇAĞLAR Öztürk", "caglar ozturk")]
    [InlineData("Işık", "isik")]
    [InlineData("İpek", "ipek")]
    public void Search_keys_ignore_case_and_turkish_letters(string input, string key)
    {
        TextNormalization.SearchKey(input).ShouldBe(key);
    }
}
