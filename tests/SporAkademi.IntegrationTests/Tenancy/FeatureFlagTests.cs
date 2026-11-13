using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Tenancy;

/// <summary>
/// P4-10 (DEC-580 – DEC-585): feature flags per school. The Platform Admin sets a school's value; the school's users read
/// their enabled flags; a disabled feature's endpoints answer exactly like an unknown route.
/// </summary>
[Trait("Story", "P4-10")]
public sealed class FeatureFlagTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SchoolId _schoolA;
    private SchoolId _schoolB;
    private HttpClient _platform = null!;
    private HttpClient _adminA = null!;
    private HttpClient _adminB = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _schoolA = await TestSchools.CreateAsync(Api, "okul-a");
        _schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        _platform = ClientAs(Api, "PlatformAdmin", null);
        _adminA = Api.SchoolAdminClient(_schoolA);
        _adminB = Api.SchoolAdminClient(_schoolB);
    }

    public override async ValueTask DisposeAsync()
    {
        _platform.Dispose();
        _adminA.Dispose();
        _adminB.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Platform_admin_sees_every_flag_of_a_school_with_its_default()
    {
        var response = await _platform.GetAsync($"/api/v1/platform/schools/{_schoolA.Value}/features", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var features = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        features.EnumerateArray().Select(f => f.GetProperty("key").GetString()).ShouldBe(FeatureCatalog.All.Select(f => f.Key));
        foreach (var feature in features.EnumerateArray())
        {
            feature.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            feature.GetProperty("defaultEnabled").GetBoolean().ShouldBeTrue();
            feature.GetProperty("overridden").GetBoolean().ShouldBeFalse();
            feature.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Setting_a_flag_stores_the_schools_own_value()
    {
        var off = await SetAsync(_schoolA, "reports", enabled: false);

        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await off.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("key").GetString().ShouldBe("reports");
        body.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        body.GetProperty("overridden").GetBoolean().ShouldBeTrue();

        // Setting it again changes the same row.
        (await SetAsync(_schoolA, "reports", enabled: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = await AcrossSchoolsAsync(db => db.Set<SchoolFeature>().IgnoreQueryFilters().AsNoTracking().ToListAsync(Ct));
        rows.ShouldHaveSingleItem();
        rows[0].TenantId.ShouldBe(_schoolA);
        rows[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_schools_and_keys_are_not_found()
    {
        var school = await SetAsync(SchoolId.From(Guid.CreateVersion7()), "reports", enabled: false);
        var key = await SetAsync(_schoolA, "no-such-flag", enabled: false);
        var list = await _platform.GetAsync($"/api/v1/platform/schools/{Guid.CreateVersion7()}/features", Ct);

        school.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(school)).ShouldBe("School.NotFound");
        key.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(key)).ShouldBe("Feature.NotFound");
        (await CodeAsync(list)).ShouldBe("School.NotFound");
    }

    [Theory]
    [InlineData("SchoolAdmin")]
    [InlineData("Coach")]
    [InlineData("Parent")]
    public async Task Only_the_platform_admin_manages_flags(string role)
    {
        using var client = ClientAs(Api, role, _schoolA);

        (await client.GetAsync($"/api/v1/platform/schools/{_schoolA.Value}/features", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/v1/platform/schools/{_schoolA.Value}/features/reports", new { enabled = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AcrossSchoolsAsync(db => db.Set<SchoolFeature>().IgnoreQueryFilters().CountAsync(Ct))).ShouldBe(0);
    }

    [Theory]
    [InlineData("SchoolAdmin")]
    [InlineData("Coach")]
    [InlineData("Parent")]
    public async Task School_users_read_their_schools_enabled_flags(string role)
    {
        await SetAsync(_schoolB, "parent-online-payments", enabled: false);
        using var client = ClientAs(Api, role, _schoolB);

        var response = await client.GetAsync("/api/v1/features", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EnabledAsync(response)).ShouldBe(["reports"]);
    }

    [Fact]
    public async Task The_platform_admin_has_no_school_flags_to_read()
    {
        (await _platform.GetAsync("/api/v1/features", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_disabled_feature_answers_like_an_unknown_route_for_that_school_only()
    {
        (await _adminA.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetAsync(_schoolA, "reports", enabled: false);

        var hidden = await _adminA.GetAsync("/api/v1/reports/revenue", Ct);
        var unknown = await _adminA.GetAsync("/api/v1/no-such-endpoint", Ct);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(hidden)).ShouldBe(await CodeAsync(unknown));
        (await _adminA.GetAsync("/api/v1/reports/attendance/csv", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // School B is untouched.
        (await _adminB.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EnabledAsync(await _adminB.GetAsync("/api/v1/features", Ct))).ShouldContain("reports");
    }

    [Fact]
    public async Task Authorization_still_comes_first_on_a_disabled_feature()
    {
        await SetAsync(_schoolA, "reports", enabled: false);
        using var coach = Api.CoachClient(_schoolA);
        using var anonymous = Api.CreateClient();

        (await coach.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await anonymous.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Parent_online_payment_is_hidden_while_its_flag_is_off()
    {
        using var parent = ClientAs(Api, "Parent", _schoolA);
        var body = new { enrollmentId = Guid.CreateVersion7(), period = "2027-01" };

        var on = await parent.PostAsJsonAsync("/api/v1/parent/online-payments", body, Ct);
        await SetAsync(_schoolA, "parent-online-payments", enabled: false);
        var off = await parent.PostAsJsonAsync("/api/v1/parent/online-payments", body, Ct);
        var status = await parent.GetAsync($"/api/v1/parent/online-payments/{Guid.CreateVersion7()}", Ct);

        (await CodeAsync(on)).ShouldNotBe("Resource.NotFound");
        off.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(off)).ShouldBe("Resource.NotFound");
        // The status of a payment already under way stays readable (DEC-584): here an unknown payment, not a hidden route.
        status.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeAsync(status)).ShouldNotBe("Resource.NotFound");
        (await EnabledAsync(await parent.GetAsync("/api/v1/features", Ct))).ShouldNotContain("parent-online-payments");
    }

    [Fact]
    public async Task A_change_invalidates_the_schools_cached_flags_at_once()
    {
        // Warm the cached values of both schools, then change school A's.
        (await EnabledAsync(await _adminA.GetAsync("/api/v1/features", Ct))).ShouldContain("reports");
        (await EnabledAsync(await _adminB.GetAsync("/api/v1/features", Ct))).ShouldContain("reports");

        await SetAsync(_schoolA, "reports", enabled: false);

        (await EnabledAsync(await _adminA.GetAsync("/api/v1/features", Ct))).ShouldNotContain("reports");
        (await _adminA.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await EnabledAsync(await _adminB.GetAsync("/api/v1/features", Ct))).ShouldContain("reports");

        await SetAsync(_schoolA, "reports", enabled: true);

        (await _adminA.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Row_level_security_shows_a_school_only_its_own_values()
    {
        await SetAsync(_schoolA, "reports", enabled: false);
        await SetAsync(_schoolB, "reports", enabled: false);
        await SetAsync(_schoolB, "parent-online-payments", enabled: false);

        var asA = await InScopeAsync(sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(_schoolA.Value);
            return sp.GetRequiredService<AppDbContext>().Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM tenancy.school_features").SingleAsync(Ct);
        });
        var platform = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM tenancy.school_features").SingleAsync(Ct));
        var moved = await InScopeAsync(sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(_schoolA.Value);
            return sp.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"UPDATE tenancy.school_features SET enabled = true WHERE tenant_id = {_schoolB.Value}", Ct);
        });

        asA.ShouldBe(1);
        platform.ShouldBe(3);
        moved.ShouldBe(0);
    }

    [Fact]
    public async Task A_school_scope_can_read_but_never_write_its_own_values()
    {
        await SetAsync(_schoolA, "reports", enabled: false);

        Task<T> AsSchoolA<T>(Func<AppDbContext, Task<T>> work) =>
            InScopeAsync(sp =>
            {
                sp.GetRequiredService<BackgroundTenantContext>().Enter(_schoolA.Value);
                return work(sp.GetRequiredService<AppDbContext>());
            });

        var read = await AsSchoolA(db => db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM tenancy.school_features").SingleAsync(Ct));
        var updated = await AsSchoolA(db => db.Database
            .ExecuteSqlAsync($"UPDATE tenancy.school_features SET enabled = true WHERE tenant_id = {_schoolA.Value}", Ct));
        var deleted = await AsSchoolA(db => db.Database
            .ExecuteSqlAsync($"DELETE FROM tenancy.school_features WHERE tenant_id = {_schoolA.Value}", Ct));
        var inserted = await Should.ThrowAsync<Npgsql.PostgresException>(() => AsSchoolA(db => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tenancy.school_features (id, tenant_id, key, enabled, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {_schoolA.Value}, 'parent-online-payments', false, now(), now())
            """,
            Ct)));

        read.ShouldBe(1);
        updated.ShouldBe(0);
        deleted.ShouldBe(0);
        inserted.SqlState.ShouldBe(Npgsql.PostgresErrorCodes.InsufficientPrivilege);
        var rows = await AcrossSchoolsAsync(db => db.Set<SchoolFeature>().AsNoTracking().ToListAsync(Ct));
        rows.ShouldHaveSingleItem().Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task The_global_default_comes_from_the_configuration_until_a_school_has_its_own_value()
    {
        await using var api = With(("Features:Defaults:reports", "false"));
        using var platform = ClientAs(api, "PlatformAdmin", null);
        using var adminA = ClientAs(api, "SchoolAdmin", _schoolA);
        using var adminB = ClientAs(api, "SchoolAdmin", _schoolB);

        (await adminA.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var listed = await (await platform.GetAsync($"/api/v1/platform/schools/{_schoolA.Value}/features", Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var reports = listed.EnumerateArray().Single(f => f.GetProperty("key").GetString() == "reports");
        reports.GetProperty("defaultEnabled").GetBoolean().ShouldBeFalse();
        reports.GetProperty("enabled").GetBoolean().ShouldBeFalse();

        // Gradual rollout: on for school A only.
        (await platform.PutAsJsonAsync($"/api/v1/platform/schools/{_schoolA.Value}/features/reports", new { enabled = true }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await adminA.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await adminB.GetAsync("/api/v1/reports/revenue", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Features:Defaults:no-such-flag", "true")]
    [InlineData("Features:Defaults:reports", "maybe")]
    public void A_wrong_default_stops_the_start(string key, string value)
    {
        using var api = With((key, value));

        Should.Throw<OptionsValidationException>(() => api.CreateClient());
    }

    private WebApplicationFactory<Program> With(params (string Key, string? Value)[] settings) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))));

    private HttpClient ClientAs(WebApplicationFactory<Program> api, string role, SchoolId? school)
    {
        var client = api.CreateClient();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), role, school?.Value, Api.Clock.GetUtcNow()));
        return client;
    }

    private Task<HttpResponseMessage> SetAsync(SchoolId school, string key, bool enabled) =>
        _platform.PutAsJsonAsync($"/api/v1/platform/schools/{school.Value}/features/{key}", new { enabled }, Ct);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private static async Task<List<string>> EnabledAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. body.GetProperty("enabled").EnumerateArray().Select(e => e.GetString()!)];
    }
}
