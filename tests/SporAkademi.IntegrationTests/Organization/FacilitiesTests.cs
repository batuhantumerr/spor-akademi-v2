using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.Application.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Organization;

[Trait("Story", "US-07")]
public sealed class FacilitiesTests(DatabaseFixture database) : IntegrationTest(database)
{
    private SchoolId _school;
    private HttpClient _admin = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _school = await TestSchools.CreateAsync(Api, "okul-a");
        _admin = Api.SchoolAdminClient(_school);
    }

    public override async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await base.DisposeAsync();
    }

    private static object NewFacility(string name = "A Sahası", string type = "Field", int? capacity = 40) => new
    {
        name,
        type,
        address = new { line = "Atatürk Cad. No:1", district = "Kadıköy", city = "İstanbul" },
        capacity,
    };

    [Fact]
    public async Task Created_facility_is_listed_with_type_address_and_capacity()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
        created.GetProperty("type").GetString().ShouldBe("Field");
        created.GetProperty("address").GetProperty("district").GetString().ShouldBe("Kadıköy");
        created.GetProperty("capacity").GetInt32().ShouldBe(40);
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/facilities", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Capacity_is_optional()
    {
        (await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility("Havuz 1", "Pool", null), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("Stadium", 10)]
    [InlineData("Field", 0)]
    public async Task Unknown_type_or_invalid_capacity_is_rejected(string type, int capacity)
    {
        (await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(type: type, capacity: capacity), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_address_parts_are_field_errors()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/facilities", new
        {
            name = "Kort",
            type = "Court",
            address = new { line = "", district = "Kadıköy", city = "" },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.GetProperty("address.line")[0].GetString().ShouldBe("Field.Required");
        errors.GetProperty("address.city")[0].GetString().ShouldBe("Field.Required");
    }

    [Fact]
    public async Task Update_and_archive_work_and_keep_history()
    {
        var created = await (await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(), Ct)).Content.ReadFromJsonAsync<FacilityDto>(Json, Ct);

        var updated = await _admin.PutAsJsonAsync($"/api/v1/facilities/{created!.Id}", new
        {
            name = "B Sahası",
            type = "Field",
            address = new { line = "Bağdat Cad. 10", district = "Maltepe", city = "İstanbul" },
            capacity = 60,
            version = created.Version,
        }, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await updated.Content.ReadFromJsonAsync<FacilityDto>(Json, Ct))!.Address.District.ShouldBe("Maltepe");

        (await _admin.PostAsync(new Uri($"/api/v1/facilities/{created.Id}/archive", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/facilities", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/facilities?includeArchived=true", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Duplicate_name_is_rejected()
    {
        await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(), Ct);

        var response = await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility("a sahası"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("Facility.NameTaken");
    }

    [Fact]
    public async Task Other_schools_cannot_see_or_change_a_facility_and_coaches_are_forbidden()
    {
        var created = await (await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(), Ct)).Content.ReadFromJsonAsync<FacilityDto>(Json, Ct);
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);
        using var coach = Api.CoachClient(_school);

        (await otherAdmin.GetAsync(new Uri($"/api/v1/facilities/{created!.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.DeleteAsync(new Uri($"/api/v1/facilities/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await coach.GetAsync(new Uri("/api/v1/facilities", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stale_version_conflicts_even_when_nothing_changes()
    {
        var created = await (await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(), Ct)).Content.ReadFromJsonAsync<FacilityDto>(Json, Ct);
        await _admin.PostAsync(new Uri($"/api/v1/facilities/{created!.Id}/archive", UriKind.Relative), null, Ct);

        var stale = await _admin.PutAsJsonAsync($"/api/v1/facilities/{created.Id}", new
        {
            name = created.Name,
            type = "Field",
            address = new { line = created.Address.Line, district = created.Address.District, city = created.Address.City },
            capacity = created.Capacity,
            version = created.Version,
        }, Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unused_facility_can_be_deleted()
    {
        var created = await (await _admin.PostAsJsonAsync("/api/v1/facilities", NewFacility(), Ct)).Content.ReadFromJsonAsync<FacilityDto>(Json, Ct);

        (await _admin.DeleteAsync(new Uri($"/api/v1/facilities/{created!.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
