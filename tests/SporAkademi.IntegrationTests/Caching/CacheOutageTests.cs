using System.Diagnostics;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Caching;

/// <summary>
/// DEC-315: the distributed cache is an optimisation, never a dependency. With the Valkey server unreachable from
/// the start, or gone in the middle, reads and writes still succeed from the database and stay fresh.
/// </summary>
public sealed class CacheOutageTests(DatabaseFixture database) : CachedReadsTest(database)
{
    /// <summary>Nothing listens on port 1, so every L2 call fails.</summary>
    private const string Unreachable = "127.0.0.1:1,password=unused,connectTimeout=500";

    private ValkeyContainer? _valkey;

    protected override ApiFactory CreateApi() =>
        new(Database.AppConnectionString, _valkey?.ConnectionString ?? Unreachable);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_valkey is not null)
        {
            await _valkey.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_unreachable_cache_server_falls_back_to_the_database_without_slowing_every_request()
    {
        // The inherited scenarios run against the unreachable server too; this one checks the latency.
        await CreateSportAsync(AdminA, "Futbol");
        (await NamesAsync(AdminA, "/api/v1/sports")).ShouldBe(["Futbol"]);

        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++)
        {
            await CreateSportAsync(AdminA, $"Branş {i}");
            (await NamesAsync(AdminA, "/api/v1/sports?pageSize=100")).Length.ShouldBe(i + 2);
        }

        // After the first failure the L2 is skipped, so 20 requests take far less than 20 connect timeouts.
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task A_cache_server_that_goes_down_degrades_to_the_database()
    {
        _valkey = new ValkeyContainer();
        await _valkey.StartAsync(Ct);
        await using var api = CreateApi();
        using var admin = api.SchoolAdminClient(SchoolA);

        await CreateSportAsync(admin, "Futbol");
        (await NamesAsync(admin, "/api/v1/sports")).ShouldBe(["Futbol"]);

        await _valkey.StopAsync(Ct);

        await CreateSportAsync(admin, "Yüzme");
        (await NamesAsync(admin, "/api/v1/sports")).ShouldBe(["Futbol", "Yüzme"]);
        (await NamesAsync(admin, "/api/v1/facilities")).ShouldBeEmpty();
    }
}
