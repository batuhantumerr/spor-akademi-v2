using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Groups;

/// <summary>
/// One school with a sport, a facility and two coaches (seeded with the identity helpers), plus helpers
/// that drive the group endpoints over HTTP.
/// </summary>
public abstract class GroupsTest(DatabaseFixture database) : IdentityTest(database)
{
    private int _userCount;

    protected SchoolId School { get; private set; }

    protected HttpClient Admin { get; private set; } = null!;

    protected Guid Football { get; private set; }

    protected Guid FieldA { get; private set; }

    protected User Ayse { get; private set; } = null!;

    protected User Mehmet { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        School = await TestSchools.CreateAsync(Api, "okul-a");
        Admin = Api.SchoolAdminClient(School);
        Football = await CreateSportAsync(Admin, "Futbol");
        FieldA = await CreateFacilityAsync(Admin, "A Sahası");
        Ayse = await SeedCoachAsync(School);
        Mehmet = await SeedCoachAsync(School);
    }

    public override async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected Task<User> SeedCoachAsync(SchoolId school) =>
        SeedUserAsync($"antrenor{Interlocked.Increment(ref _userCount)}@okul.test", UserRole.Coach, school);

    protected HttpClient CoachClient(User coach) => Api.CoachClient(coach.TenantId!.Value, coach.Id.Value);

    protected static async Task<Guid> CreateSportAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/sports", new { name }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    protected static async Task<Guid> CreateFacilityAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/facilities", new
        {
            name,
            type = "Field",
            address = new { line = "Atatürk Cad. No:1", district = "Kadıköy", city = "İstanbul" },
            capacity = 60,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    protected object NewGroup(
        string name = "U10 Futbol",
        Guid? coachId = null,
        Guid? facilityId = null,
        Guid? sportId = null,
        int capacity = 20,
        int from = 2015,
        int to = 2016) => new
        {
            sportId = sportId ?? Football,
            name,
            birthYearRange = new { from, to },
            capacity,
            facilityId = facilityId ?? FieldA,
            coachId,
        };

    protected Task<HttpResponseMessage> PostGroupAsync(object body, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync("/api/v1/groups", body, Ct);

    protected async Task<JsonElement> CreateGroupAsync(string name = "U10 Futbol", User? coach = null, Guid? facilityId = null, HttpClient? client = null)
    {
        var response = await PostGroupAsync(NewGroup(name, coach?.Id.Value, facilityId), client);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    protected static object Slot(string day, string start, string end) => new { day, start, end };

    protected Task<HttpResponseMessage> PutScheduleAsync(JsonElement group, uint? version = null, params object[] slots) =>
        Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}/schedule", new { slots, version = version ?? VersionOf(group) }, Ct);

    /// <summary>Saves the schedule and returns the group as now stored.</summary>
    protected async Task<JsonElement> ScheduleAsync(JsonElement group, params object[] slots)
    {
        var response = await PutScheduleAsync(group, null, slots);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await GetGroupAsync(IdOf(group));
    }

    protected async Task<JsonElement> GetGroupAsync(Guid id, HttpClient? client = null) =>
        await (client ?? Admin).GetFromJsonAsync<JsonElement>($"/api/v1/groups/{id}", Ct);

    protected Task<HttpResponseMessage> ArchiveAsync(Guid id, HttpClient? client = null) =>
        (client ?? Admin).PostAsync(new Uri($"/api/v1/groups/{id}/archive", UriKind.Relative), null, Ct);

    protected static Guid IdOf(JsonElement element) => element.GetProperty("id").GetGuid();

    protected static uint VersionOf(JsonElement element) => element.GetProperty("version").GetUInt32();

    protected static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    protected static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();
}
