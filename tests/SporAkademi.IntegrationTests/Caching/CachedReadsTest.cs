using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Caching;

/// <summary>
/// The cached reads (DEC-311 – DEC-313): sports and facility lists, the dashboard and the school's time zone. Every
/// scenario runs with the in-memory cache only and with a real Valkey L2 (the two derived classes). Each one warms
/// the cache first, so a leak or a stale entry would be served from it.
/// </summary>
[Trait("Story", "US-02")]
public abstract class CachedReadsTest(DatabaseFixture database) : IntegrationTest(database)
{
    /// <summary>The cached GET endpoints; every one is probed for cross-school leaks.</summary>
    private static readonly string[] CachedEndpoints =
    [
        "/api/v1/sports",
        "/api/v1/sports?includeArchived=true",
        "/api/v1/facilities",
        "/api/v1/facilities?includeArchived=true&page=1&pageSize=100",
        "/api/v1/dashboard",
    ];

    private int _phones;

    protected SchoolId SchoolA { get; private set; }

    protected HttpClient AdminA { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        SchoolA = await TestSchools.CreateAsync(Api, "okul-a");
        AdminA = Api.SchoolAdminClient(SchoolA);
    }

    public override async ValueTask DisposeAsync()
    {
        AdminA.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Another_school_never_gets_this_schools_cached_reads()
    {
        var sport = await CreateSportAsync(AdminA, "Futbol A");
        var facility = await CreateFacilityAsync(AdminA, "Saha A");
        await RegisterStudentAsync(AdminA);
        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        using var adminB = Api.SchoolAdminClient(schoolB);

        // Warm every cached read for school A, then read the same endpoints as school B.
        foreach (var url in CachedEndpoints)
        {
            (await AdminA.GetAsync(new Uri(url, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var leaks = new List<string>();
        foreach (var url in CachedEndpoints)
        {
            var body = await adminB.GetStringAsync(new Uri(url, UriKind.Relative), Ct);
            foreach (var secret in new[] { sport.ToString(), facility.ToString(), "Futbol A", "Saha A" })
            {
                if (body.Contains(secret, StringComparison.OrdinalIgnoreCase))
                {
                    leaks.Add($"{url} holds {secret}");
                }
            }
        }

        leaks.ShouldBeEmpty();
        (await NamesAsync(adminB, "/api/v1/sports")).ShouldBeEmpty();
        (await NamesAsync(adminB, "/api/v1/facilities")).ShouldBeEmpty();
        (await adminB.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", Ct)).GetProperty("kpis").GetProperty("activeStudents").GetInt32().ShouldBe(0);

        // And the other way round: B's writes and warm entries do not reach A.
        await CreateSportAsync(adminB, "Yüzme B");
        (await NamesAsync(adminB, "/api/v1/sports")).ShouldBe(["Yüzme B"]);
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Futbol A"]);
        (await AdminA.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", Ct)).GetProperty("kpis").GetProperty("activeStudents").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Sport_list_is_fresh_after_create_rename_archive_and_delete()
    {
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBeEmpty();

        var futbol = await CreateSportAsync(AdminA, "Futbol");
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Futbol"]);

        (await AdminA.PutAsJsonAsync($"/api/v1/sports/{futbol}", new { name = "Basketbol", version = await VersionAsync($"/api/v1/sports/{futbol}") }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Basketbol"]);

        (await AdminA.PostAsync(new Uri($"/api/v1/sports/{futbol}/archive", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBeEmpty();
        (await NamesAsync(AdminA, "/api/v1/sports?includeArchived=true")).ShouldBe(["Basketbol"]);

        (await AdminA.DeleteAsync(new Uri($"/api/v1/sports/{futbol}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await NamesAsync(AdminA, "/api/v1/sports?includeArchived=true")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Facility_list_is_fresh_after_create_update_and_archive()
    {
        (await NamesAsync(AdminA, "/api/v1/facilities")).ShouldBeEmpty();

        var facility = await CreateFacilityAsync(AdminA, "Saha 1");
        (await NamesAsync(AdminA, "/api/v1/facilities")).ShouldBe(["Saha 1"]);

        var version = await VersionAsync($"/api/v1/facilities/{facility}");
        (await AdminA.PutAsJsonAsync($"/api/v1/facilities/{facility}", FacilityBody("Kapalı Salon", version), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NamesAsync(AdminA, "/api/v1/facilities")).ShouldBe(["Kapalı Salon"]);

        (await AdminA.PostAsync(new Uri($"/api/v1/facilities/{facility}/archive", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NamesAsync(AdminA, "/api/v1/facilities")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dashboard_is_fresh_after_any_write_of_the_school()
    {
        (await ActiveStudentsAsync()).ShouldBe(0);

        await RegisterStudentAsync(AdminA);

        (await ActiveStudentsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Reads_come_from_the_cache_until_the_school_writes()
    {
        var futbol = await CreateSportAsync(AdminA, "Futbol");
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Futbol"]);

        // A change behind the application's back (no EF save, so no invalidation) is not seen: the list is cached.
        await RenameBehindTheCacheAsync(futbol, "Hentbol");
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Futbol"]);

        // Any saved change to the school's sports invalidates the list.
        await CreateSportAsync(AdminA, "Yüzme");
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Hentbol", "Yüzme"]);
    }

    protected static async Task<Guid> CreateSportAsync(HttpClient client, string name)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.PostAsJsonAsync("/api/v1/sports", new { name }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    protected static async Task<string[]> NamesAsync(HttpClient client, string url)
    {
        ArgumentNullException.ThrowIfNull(client);
        var list = await client.GetFromJsonAsync<JsonElement>(url, Ct);
        return [.. list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];
    }

    /// <summary>Changes a sport's name with plain SQL, bypassing EF Core and therefore the cache invalidation.</summary>
    protected async Task RenameBehindTheCacheAsync(Guid sport, string name)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE organization.sports SET name = @name, name_key = lower(@name) WHERE id = @id", connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("id", sport);
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static async Task<Guid> CreateFacilityAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/facilities", FacilityBody(name), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static object FacilityBody(string name, uint? version = null) => new
    {
        name,
        type = "Field",
        address = new { line = "Spor Cd. 1", district = "Kadıköy", city = "İstanbul" },
        capacity = 20,
        version,
    };

    private async Task<uint> VersionAsync(string url) =>
        (await AdminA.GetFromJsonAsync<JsonElement>(url, Ct)).GetProperty("version").GetUInt32();

    private async Task<int> ActiveStudentsAsync() =>
        (await AdminA.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", Ct)).GetProperty("kpis").GetProperty("activeStudents").GetInt32();

    private async Task RegisterStudentAsync(HttpClient client)
    {
        var n = Interlocked.Increment(ref _phones);
        var response = await client.PostAsJsonAsync("/api/v1/students", new
        {
            firstName = "Ali",
            lastName = "Deneme",
            birthDate = "2015-05-01",
            gender = "Male",
            phone = Phone(n, 1),
            emergencyContact = new { name = "Acil Kişi", phone = Phone(n, 2), relationship = "Teyze" },
            guardians = new[]
            {
                new { newGuardian = new { firstName = "Veli", lastName = "Deneme", phone = Phone(n, 3), email = "veli@example.com" }, relationship = "Mother" },
            },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static string Phone(int student, int role) =>
        string.Create(CultureInfo.InvariantCulture, $"0533 {role}{student / 100 % 100:00} {student % 100:00} 00");
}

/// <summary>The cached reads with the in-memory cache only (no <c>ConnectionStrings:Cache</c>, like tests and local runs).</summary>
public sealed class CachedReadsInMemoryTests(DatabaseFixture database) : CachedReadsTest(database);

/// <summary>The cached reads with a real Valkey L2, plus what only a shared L2 can show: a second API instance.</summary>
public sealed class CachedReadsWithValkeyTests(DatabaseFixture database, ValkeyFixture valkey)
    : CachedReadsTest(database), IClassFixture<ValkeyFixture>
{
    protected override ApiFactory CreateApi() => new(Database.AppConnectionString, valkey.Valkey.ConnectionString);

    [Fact]
    public async Task Another_api_instance_reads_the_shared_cache_and_its_invalidations()
    {
        var futbol = await CreateSportAsync(AdminA, "Futbol");
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Futbol"]);
        await RenameBehindTheCacheAsync(futbol, "Hentbol");

        // A second instance has an empty L1, so the stale name can only come from Valkey.
        await using (var second = CreateApi())
        {
            using var admin = second.SchoolAdminClient(SchoolA);
            (await NamesAsync(admin, "/api/v1/sports")).ShouldBe(["Futbol"]);
        }

        // A write on the first instance invalidates the tag in Valkey too, so a third instance reads fresh data.
        await CreateSportAsync(AdminA, "Yüzme");
        await using var third = CreateApi();
        using var thirdAdmin = third.SchoolAdminClient(SchoolA);
        (await NamesAsync(thirdAdmin, "/api/v1/sports")).ShouldBe(["Hentbol", "Yüzme"]);
    }
}
