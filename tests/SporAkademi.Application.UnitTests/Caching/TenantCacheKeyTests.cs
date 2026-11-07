using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.UnitTests.Caching;

/// <summary>DEC-312: cache keys and tags always carry the school and never free text.</summary>
[Trait("Story", "US-02")]
public sealed class TenantCacheKeyTests
{
    private static readonly SchoolId SchoolA = SchoolId.From(Guid.Parse("01890000-0000-7000-8000-00000000000a"));
    private static readonly SchoolId SchoolB = SchoolId.From(Guid.Parse("01890000-0000-7000-8000-00000000000b"));

    [Fact]
    public void Key_starts_with_the_school_and_the_area()
    {
        var key = TenantCacheKey.For(SchoolA, CacheArea.Sports, "list", true, 2, 50);

        key.Value.ShouldBe("sa:v1:t:0189000000007000800000000000000a:sports:list:1:2:50");
        key.Tenant.ShouldBe(SchoolA);
        key.Area.ShouldBe(CacheArea.Sports);
    }

    [Fact]
    public void Entries_are_tagged_with_the_schools_area_and_the_school()
    {
        var key = TenantCacheKey.For(SchoolA, CacheArea.Facilities, "list");

        key.Tags.ShouldBe([TenantCacheKey.AreaTag(SchoolA, CacheArea.Facilities), TenantCacheKey.TenantTag(SchoolA)]);
        key.Value.ShouldStartWith(TenantCacheKey.AreaTag(SchoolA, CacheArea.Facilities));
    }

    [Fact]
    public void Same_read_of_two_schools_never_shares_a_key_or_a_tag()
    {
        var a = TenantCacheKey.For(SchoolA, CacheArea.Dashboard, "summary", new DateOnly(2027, 1, 14));
        var b = TenantCacheKey.For(SchoolB, CacheArea.Dashboard, "summary", new DateOnly(2027, 1, 14));

        a.Value.ShouldNotBe(b.Value);
        a.Tags.Intersect(b.Tags).ShouldBeEmpty();
    }

    [Fact]
    public void Different_parameters_give_different_keys()
    {
        TenantCacheKey.For(SchoolA, CacheArea.Sports, "list", false, 1, 20).Value
            .ShouldNotBe(TenantCacheKey.For(SchoolA, CacheArea.Sports, "list", true, 1, 20).Value);
        TenantCacheKey.For(SchoolA, CacheArea.Sports, "list", false, 1, 20).Value
            .ShouldNotBe(TenantCacheKey.For(SchoolA, CacheArea.Facilities, "list", false, 1, 20).Value);
        TenantCacheKey.For(SchoolA, CacheArea.Dashboard, "summary", new DateOnly(2027, 1, 14)).Value
            .ShouldEndWith(":summary:2027-01-14");
    }

    [Fact]
    public void A_key_without_a_school_is_rejected()
    {
        Should.Throw<ArgumentException>(() => TenantCacheKey.For(default, CacheArea.Sports, "list"));
        Should.Throw<ArgumentException>(() => TenantCacheKey.TenantTag(default));
        Should.Throw<ArgumentException>(() => TenantCacheKey.AreaTag(SchoolId.From(Guid.Empty), CacheArea.School));
    }

    [Theory]
    [InlineData("Ali Veli")]
    [InlineData("ali@example.com")]
    [InlineData("LIST")]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("0123456789012345678901234567890123")]
    public void Free_text_cannot_become_part_of_a_key(string part) =>
        Should.Throw<ArgumentException>(() => TenantCacheKey.For(SchoolA, CacheArea.Sports, part));

    [Fact]
    public void Other_part_types_are_rejected()
    {
        Should.Throw<ArgumentException>(() => TenantCacheKey.For(SchoolA, CacheArea.Sports, Guid.NewGuid()));
        Should.Throw<ArgumentException>(() => TenantCacheKey.For(SchoolA, CacheArea.Sports, 1.5m));
    }

    [Fact]
    public void Unknown_area_is_rejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TenantCacheKey.For(SchoolA, (CacheArea)99, "list"));

    [Fact]
    public void Keys_have_a_length_limit()
    {
        var parts = Enumerable.Repeat<object>("abcdefghijklmnopqrstuvwxyz012345", 6).ToArray();

        Should.Throw<ArgumentException>(() => TenantCacheKey.For(SchoolA, CacheArea.Sports, parts));
    }

    [Fact]
    public void A_new_school_id_is_a_valid_tenant() =>
        TenantCacheKey.For(EntityId.New<SchoolId>(), CacheArea.School, "time-zone").Value.ShouldEndWith(":school:time-zone");
}
