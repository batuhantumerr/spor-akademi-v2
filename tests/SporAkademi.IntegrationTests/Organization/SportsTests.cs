using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.Application.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Organization;

[Trait("Story", "US-06")]
public sealed class SportsTests(DatabaseFixture database) : IntegrationTest(database)
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

    [Fact]
    public async Task Created_sport_is_listed()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/sports", new { name = "Futbol" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldStartWith("/api/v1/sports/");
        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/sports", Ct);
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ShouldBe(["Futbol"]);
        list.GetProperty("totalCount").GetInt32().ShouldBe(1);
        list.GetProperty("totalPages").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("Futbol")]
    [InlineData("futbol")]
    [InlineData("  FUTBOL ")]
    public async Task Duplicate_name_in_the_same_school_is_rejected(string duplicate)
    {
        await CreateAsync("Futbol");

        var response = await _admin.PostAsJsonAsync("/api/v1/sports", new { name = duplicate }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Sport.NameTaken");
    }

    [Fact]
    public async Task Turkish_casing_is_respected_for_uniqueness()
    {
        await CreateAsync("İzci Oyunları");

        (await _admin.PostAsJsonAsync("/api/v1/sports", new { name = "izci oyunları" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Names_are_unique_per_school_not_globally()
    {
        await CreateAsync("Futbol");
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(otherSchool);

        (await otherAdmin.PostAsJsonAsync("/api/v1/sports", new { name = "Futbol" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Archived_sports_are_hidden_unless_requested()
    {
        var sport = await CreateAsync("Yüzme");

        (await _admin.PostAsync(new Uri($"/api/v1/sports/{sport.Id}/archive", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/sports", Ct)).GetProperty("items").GetArrayLength().ShouldBe(0);
        var all = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/sports?includeArchived=true", Ct);
        all.GetProperty("items")[0].GetProperty("isArchived").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Rename_uses_the_version_to_detect_lost_updates()
    {
        var sport = await CreateAsync("Basketbol");

        var first = await _admin.PutAsJsonAsync($"/api/v1/sports/{sport.Id}", new { name = "Basketbol (U12)", version = sport.Version }, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<SportDto>(Ct))!.Name.ShouldBe("Basketbol (U12)");

        var stale = await _admin.PutAsJsonAsync($"/api/v1/sports/{sport.Id}", new { name = "Basketbol (U14)", version = sport.Version }, Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(stale)).ShouldBe("Concurrency.Conflict");
    }

    [Fact]
    public async Task Unused_sport_can_be_deleted()
    {
        var sport = await CreateAsync("Tenis");

        (await _admin.DeleteAsync(new Uri($"/api/v1/sports/{sport.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.GetAsync(new Uri($"/api/v1/sports/{sport.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Blank_name_is_a_field_error()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/sports", new { name = "  " }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errors").GetProperty("name")[0].GetString().ShouldBe("Field.Required");
    }

    [Fact]
    public async Task Coaches_cannot_manage_sports()
    {
        using var coach = Api.CoachClient(_school);

        (await coach.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("page=2147483647&pageSize=100", "page")]
    public async Task Out_of_range_paging_is_a_field_error(string query, string field)
    {
        var response = await _admin.GetAsync(new Uri($"/api/v1/sports?{query}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_creates_with_the_same_name_never_fail_with_500()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _admin.PostAsJsonAsync("/api/v1/sports", new { name = "Voleybol" }, Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SportDto> CreateAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/sports", new { name }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SportDto>(Ct))!;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();
}
