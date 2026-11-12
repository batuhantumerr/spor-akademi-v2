using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Caching;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.IntegrationTests.Caching;

/// <summary>DEC-312 and DEC-313 without a database: which tags a change invalidates, and the tenant guard of the cache.</summary>
[Trait("Story", "US-02")]
public sealed class CacheRulesTests
{
    private static readonly SchoolId SchoolA = EntityId.New<SchoolId>();
    private static readonly SchoolId SchoolB = EntityId.New<SchoolId>();

    [Fact]
    public void A_sport_change_invalidates_the_schools_sports_and_dashboard()
    {
        var sport = Sport.Create(SchoolA, "Futbol").Value;

        CacheInvalidationInterceptor.TagsFor(sport, SchoolA.Value).ShouldBe(
            [TenantCacheKey.AreaTag(SchoolA, CacheArea.Dashboard), TenantCacheKey.AreaTag(SchoolA, CacheArea.Reports), TenantCacheKey.AreaTag(SchoolA, CacheArea.Sports)],
            ignoreOrder: true);
    }

    [Fact]
    public void A_facility_change_invalidates_the_schools_facilities_and_dashboard()
    {
        var facility = Facility.Create(SchoolA, "Saha", FacilityType.Field, Address.Create("Cd. 1", "Kadıköy", "İstanbul").Value, null).Value;

        CacheInvalidationInterceptor.TagsFor(facility, null).ShouldBe(
            [TenantCacheKey.AreaTag(SchoolA, CacheArea.Dashboard), TenantCacheKey.AreaTag(SchoolA, CacheArea.Reports), TenantCacheKey.AreaTag(SchoolA, CacheArea.Facilities)],
            ignoreOrder: true);
    }

    [Fact]
    public void A_school_change_invalidates_its_settings_and_dashboard()
    {
        var school = School.Create("Okul", Slug.Create("okul").Value, "Europe/Istanbul", RetentionPolicy.Default).Value;

        CacheInvalidationInterceptor.TagsFor(school, null).ShouldBe(
            [TenantCacheKey.AreaTag(school.Id, CacheArea.School), TenantCacheKey.AreaTag(school.Id, CacheArea.Dashboard), TenantCacheKey.AreaTag(school.Id, CacheArea.Reports)],
            ignoreOrder: true);
    }

    [Fact]
    public void Any_other_change_of_a_school_invalidates_its_dashboard_and_reports_only() =>
        CacheInvalidationInterceptor.TagsFor(new OtherRow(SchoolB), SchoolB.Value)
            .ShouldBe([TenantCacheKey.AreaTag(SchoolB, CacheArea.Dashboard), TenantCacheKey.AreaTag(SchoolB, CacheArea.Reports)]);

    [Fact]
    public void The_tags_follow_the_changed_rows_school_not_the_callers() =>
        CacheInvalidationInterceptor.TagsFor(new OtherRow(SchoolB), SchoolA.Value)
            .ShouldBe([TenantCacheKey.AreaTag(SchoolB, CacheArea.Dashboard), TenantCacheKey.AreaTag(SchoolB, CacheArea.Reports)]);

    [Fact]
    public void A_child_row_without_a_tenant_invalidates_the_requests_school_dashboard()
    {
        CacheInvalidationInterceptor.TagsFor(new object(), SchoolA.Value).ShouldBe([TenantCacheKey.AreaTag(SchoolA, CacheArea.Dashboard), TenantCacheKey.AreaTag(SchoolA, CacheArea.Reports)]);
        CacheInvalidationInterceptor.TagsFor(new object(), null).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_school_user_cannot_read_or_fill_another_schools_entries()
    {
        using var host = NewCacheHost(out var distributed);
        var cache = new HybridTenantCache(host.Cache, distributed, UserOf(SchoolA.Value));

        await Should.ThrowAsync<TenantIsolationException>(async () =>
            await cache.GetOrCreateAsync(TenantCacheKey.For(SchoolB, CacheArea.Sports, "list"), _ => ValueTask.FromResult(1), CancellationToken.None));
        (await cache.GetOrCreateAsync(TenantCacheKey.For(SchoolA, CacheArea.Sports, "list"), _ => ValueTask.FromResult(2), CancellationToken.None)).ShouldBe(2);
    }

    [Fact]
    public async Task Callers_without_a_school_may_address_a_school_explicitly()
    {
        using var host = NewCacheHost(out var distributed);
        var cache = new HybridTenantCache(host.Cache, distributed, UserOf(null));

        (await cache.GetOrCreateAsync(TenantCacheKey.For(SchoolB, CacheArea.School, "time-zone"), _ => ValueTask.FromResult("Europe/Istanbul"), CancellationToken.None))
            .ShouldBe("Europe/Istanbul");
    }

    [Fact]
    public async Task A_waiting_caller_retries_with_its_own_factory_when_the_first_callers_services_are_gone()
    {
        using var host = NewCacheHost(out var distributed);
        var key = TenantCacheKey.For(SchoolA, CacheArea.Sports, "list");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        var secondFactoryRunsBeforeRelease = false;
        using var firstRequest = new CancellationTokenSource();

        // The first request's factory waits, then finds its DbContext disposed because that request has ended.
        var first = new HybridTenantCache(host.Cache, distributed, UserOf(SchoolA.Value)).GetOrCreateAsync<int>(
            key,
            async _ =>
            {
                firstStarted.SetResult();
                await release.Task;
                throw new ObjectDisposedException("AppDbContext");
            },
            firstRequest.Token).AsTask();
        await firstStarted.Task;

        // The second request joins the running factory instead of starting its own.
        var second = new HybridTenantCache(host.Cache, distributed, UserOf(SchoolA.Value)).GetOrCreateAsync(
            key,
            _ =>
            {
                secondFactoryRunsBeforeRelease |= !Volatile.Read(ref released);
                return ValueTask.FromResult(42);
            },
            CancellationToken.None).AsTask();

        await firstRequest.CancelAsync();
        Volatile.Write(ref released, true);
        release.SetResult();

        (await second).ShouldBe(42);
        secondFactoryRunsBeforeRelease.ShouldBeFalse();
        await Should.ThrowAsync<Exception>(() => first);
    }

    [Fact]
    public async Task Without_a_connection_string_there_is_no_distributed_cache()
    {
        using var host = NewCacheHost(out var distributed);

        distributed.IsConfigured.ShouldBeFalse();
        (await distributed.GetAsync("any", CancellationToken.None)).ShouldBeNull();
    }

    private static CacheHost NewCacheHost(out ResilientDistributedCache distributed)
    {
        distributed = new ResilientDistributedCache(new ConfigurationBuilder().Build(), NullLogger<ResilientDistributedCache>.Instance);
        return new CacheHost(distributed, NullLoggerFactory.Instance);
    }

    private static FixedUser UserOf(Guid? tenant) => new(tenant);

    private sealed class FixedUser(Guid? tenant) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.CreateVersion7();

        public Guid? TenantId => tenant;

        public Role? Role => tenant is null ? Application.Abstractions.Authentication.Role.PlatformAdmin : Application.Abstractions.Authentication.Role.SchoolAdmin;
    }

    private sealed class OtherRow(SchoolId tenant) : ITenantScoped
    {
        public SchoolId TenantId { get; } = tenant;
    }
}
