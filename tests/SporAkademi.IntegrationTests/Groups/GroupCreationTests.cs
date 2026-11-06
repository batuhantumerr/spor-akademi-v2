using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Groups;

[Trait("Story", "US-09")]
public sealed class GroupCreationTests(DatabaseFixture database) : GroupsTest(database)
{
    [Fact]
    public async Task Group_is_created_with_sport_birth_years_capacity_facility_and_coach()
    {
        var response = await PostGroupAsync(NewGroup("U10 Futbol", Ayse.Id.Value));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var group = await BodyOf(response);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/groups/{IdOf(group)}");
        group.GetProperty("name").GetString().ShouldBe("U10 Futbol");
        group.GetProperty("status").GetString().ShouldBe("Active");
        group.GetProperty("sport").GetProperty("id").GetGuid().ShouldBe(Football);
        group.GetProperty("sport").GetProperty("name").GetString().ShouldBe("Futbol");
        group.GetProperty("facility").GetProperty("name").GetString().ShouldBe("A Sahası");
        group.GetProperty("birthYearRange").GetProperty("from").GetInt32().ShouldBe(2015);
        group.GetProperty("birthYearRange").GetProperty("to").GetInt32().ShouldBe(2016);
        group.GetProperty("capacity").GetInt32().ShouldBe(20);
        var coach = group.GetProperty("coach");
        coach.GetProperty("id").GetGuid().ShouldBe(Ayse.Id.Value);
        coach.GetProperty("firstName").GetString().ShouldBe("Deniz");
        coach.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        group.GetProperty("slots").GetArrayLength().ShouldBe(0);
        VersionOf(group).ShouldBeGreaterThan(0u);
    }

    [Fact]
    public async Task Group_can_be_created_without_a_coach()
    {
        var group = await CreateGroupAsync();

        group.GetProperty("coach").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(2017, 2016)]
    [InlineData(1800, 2016)]
    public async Task Invalid_birth_year_range_is_rejected(int from, int to)
    {
        var response = await PostGroupAsync(NewGroup(from: from, to: to));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("birthYearRange")[0].GetString().ShouldBe("BirthYearRange.Invalid");
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/groups", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Capacity_below_one_is_rejected(int capacity)
    {
        var response = await PostGroupAsync(NewGroup(capacity: capacity));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("capacity")[0].GetString().ShouldBe("Group.InvalidCapacity");
    }

    [Fact]
    public async Task Names_are_unique_per_school_ignoring_turkish_case()
    {
        await CreateGroupAsync("İleri Grup");

        var response = await PostGroupAsync(NewGroup("ileri grup"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Group.NameTaken");
    }

    [Fact]
    public async Task Archived_sport_and_facility_cannot_get_new_groups()
    {
        var archivedSport = await CreateSportAsync(Admin, "Hentbol");
        await Admin.PostAsync(new Uri($"/api/v1/sports/{archivedSport}/archive", UriKind.Relative), null, Ct);
        var archivedFacility = await CreateFacilityAsync(Admin, "Eski Saha");
        await Admin.PostAsync(new Uri($"/api/v1/facilities/{archivedFacility}/archive", UriKind.Relative), null, Ct);

        var sportResponse = await PostGroupAsync(NewGroup(sportId: archivedSport));
        var facilityResponse = await PostGroupAsync(NewGroup(facilityId: archivedFacility));

        sportResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(sportResponse)).ShouldBe("Sport.Archived");
        facilityResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(facilityResponse)).ShouldBe("Facility.Archived");
    }

    [Fact]
    public async Task Unknown_sport_or_facility_is_not_found()
    {
        var sportResponse = await PostGroupAsync(NewGroup(sportId: Guid.CreateVersion7()));
        var facilityResponse = await PostGroupAsync(NewGroup(facilityId: Guid.CreateVersion7()));

        (await CodeOf(sportResponse)).ShouldBe("Sport.NotFound");
        (await CodeOf(facilityResponse)).ShouldBe("Facility.NotFound");
    }

    [Fact]
    public async Task Coach_must_be_an_active_coach_of_the_school()
    {
        var admin = await SeedUserAsync("mudur@okul.test", Domain.Identity.UserRole.SchoolAdmin, School);
        var leaver = await SeedCoachAsync(School);
        await DeactivateUserAsync(leaver.Id);

        foreach (var coachId in new[] { Guid.CreateVersion7(), admin.Id.Value, leaver.Id.Value })
        {
            var response = await PostGroupAsync(NewGroup($"Grup {coachId}", coachId));

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CodeOf(response)).ShouldBe("Group.CoachNotFound");
        }
    }

    [Fact]
    public async Task List_filters_by_sport_coach_and_status_and_is_paged()
    {
        var swimming = await CreateSportAsync(Admin, "Yüzme");
        await CreateGroupAsync("A Futbol", Ayse);
        await CreateGroupAsync("B Futbol", Mehmet);
        var swim = await PostGroupAsync(NewGroup("C Yüzme", Ayse.Id.Value, sportId: swimming));
        var archived = await CreateGroupAsync("D Eski");
        await ArchiveAsync(IdOf(archived));

        async Task<string?[]> Names(string query) =>
            [.. (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups{query}", Ct)).GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("name").GetString())];

        swim.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Names("")).ShouldBe(["A Futbol", "B Futbol", "C Yüzme", "D Eski"]);
        (await Names($"?sportId={swimming}")).ShouldBe(["C Yüzme"]);
        (await Names($"?coachId={Ayse.Id.Value}")).ShouldBe(["A Futbol", "C Yüzme"]);
        (await Names("?status=Archived")).ShouldBe(["D Eski"]);
        (await Names("?status=Active&pageSize=2&page=2")).ShouldBe(["C Yüzme"]);
        var page = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/groups?pageSize=2", Ct);
        page.GetProperty("totalCount").GetInt32().ShouldBe(4);
        page.GetProperty("totalPages").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Coaches_cannot_create_or_list_groups()
    {
        using var coach = CoachClient(Ayse);

        (await PostGroupAsync(NewGroup(), coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.GetAsync(new Uri("/api/v1/groups", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
