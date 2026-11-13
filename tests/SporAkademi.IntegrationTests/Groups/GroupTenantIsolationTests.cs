using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Groups;

/// <summary>US-02 and ADR-0003 applied to groups: other schools' groups do not exist for the caller.</summary>
[Trait("Story", "US-02")]
public sealed class GroupTenantIsolationTests(DatabaseFixture database) : GroupsTest(database)
{
    private SchoolId _schoolB;
    private HttpClient _adminB = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        _adminB = Api.SchoolAdminClient(_schoolB);
    }

    public override async ValueTask DisposeAsync()
    {
        _adminB.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Another_schools_group_is_not_found_for_every_operation()
    {
        var group = await ScheduleAsync(await CreateGroupAsync("U10 Futbol", Ayse), Slot("Monday", "17:00", "18:00"));
        var id = IdOf(group);

        var responses = new[]
        {
            await _adminB.GetAsync(new Uri($"/api/v1/groups/{id}", UriKind.Relative), Ct),
            await _adminB.PutAsJsonAsync($"/api/v1/groups/{id}", new
            {
                name = "Hack",
                birthYearRange = new { from = 2015, to = 2016 },
                capacity = 10,
                facilityId = FieldA,
                version = VersionOf(group),
            }, Ct),
            await _adminB.PutAsJsonAsync($"/api/v1/groups/{id}/schedule", new { slots = Array.Empty<object>(), version = VersionOf(group) }, Ct),
            await _adminB.PostAsync(new Uri($"/api/v1/groups/{id}/archive", UriKind.Relative), null, Ct),
            await _adminB.GetAsync(new Uri($"/api/v1/groups/{id}/sessions?from=2027-01-11&to=2027-01-17", UriKind.Relative), Ct),
        };

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("Group.NotFound");
        }

        (await _adminB.GetFromJsonAsync<JsonElement>("/api/v1/groups", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        var unchanged = await GetGroupAsync(id);
        unchanged.GetProperty("name").GetString().ShouldBe("U10 Futbol");
        unchanged.GetProperty("slots").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Another_schools_coach_does_not_see_the_group()
    {
        var group = await CreateGroupAsync("U10 Futbol", Ayse);
        var coachOfB = await SeedCoachAsync(_schoolB);
        using var coach = CoachClient(coachOfB);

        (await coach.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/groups", Ct)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_group_cannot_reference_another_schools_sport_facility_or_coach()
    {
        var sportOfB = await CreateSportAsync(_adminB, "Futbol");
        var facilityOfB = await CreateFacilityAsync(_adminB, "B Sahası");
        var coachOfB = await SeedCoachAsync(_schoolB);

        var sport = await PostGroupAsync(NewGroup(sportId: sportOfB));
        var facility = await PostGroupAsync(NewGroup(facilityId: facilityOfB));
        var coach = await PostGroupAsync(NewGroup(coachId: coachOfB.Id.Value));

        (await CodeOf(sport)).ShouldBe("Sport.NotFound");
        (await CodeOf(facility)).ShouldBe("Facility.NotFound");
        (await CodeOf(coach)).ShouldBe("Group.CoachNotFound");
    }

    [Fact]
    [Trait("Story", "US-09")]
    public async Task Composite_foreign_key_rejects_a_sport_of_another_school()
    {
        // ADR-0003 layer 3, below the application: even a direct insert cannot mix schools.
        var sportOfB = SportId.From(await CreateSportAsync(_adminB, "Yüzme"));

        var violation = await InScopeAsync(async sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(School.Value);
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(Group.Create(School, sportOfB, FacilityId.From(FieldA), null, "Sızma", BirthYearRange.Create(2015, 2016).Value, 10).Value);
            return await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });

        var postgres = violation.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe("23503");
        postgres.ConstraintName.ShouldBe("fk_groups_sport_tenant_id_sport_id");
    }

    [Fact]
    public async Task Group_names_are_unique_per_school_not_globally()
    {
        await CreateGroupAsync("U10 Futbol");
        var sportOfB = await CreateSportAsync(_adminB, "Futbol");
        var facilityOfB = await CreateFacilityAsync(_adminB, "B Sahası");

        var response = await PostGroupAsync(NewGroup("U10 Futbol", sportId: sportOfB, facilityId: facilityOfB), _adminB);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
