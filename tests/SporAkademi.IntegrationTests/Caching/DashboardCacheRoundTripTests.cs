using System.Net.Http.Json;
using Npgsql;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Caching;

/// <summary>
/// DEC-314, DEC-410: the dashboard and the reports survive the System.Text.Json round trip through Valkey unchanged (money as text, decimal
/// ratios, DateOnly weeks). A second API instance with an empty L1 must return exactly what the first one returned.
/// </summary>
[Trait("Story", "US-02")]
public sealed class DashboardCacheRoundTripTests(DatabaseFixture database, ValkeyFixture valkey)
    : BillingTest(database), IClassFixture<ValkeyFixture>
{
    protected override ApiFactory CreateApi() => new(Database.AppConnectionString, valkey.Valkey.ConnectionString);

    [Fact]
    public async Task A_second_instance_returns_the_same_dashboard_from_valkey()
    {
        var (_, group, enrollment) = await EnrolledAsync("U10 Futbol", fee: "1000.00");
        await RecordOkAsync(enrollment, "2027-01", "400.50", paidOn: "2027-01-10");

        var first = await Admin.GetStringAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct);
        first.ShouldContain("400.50");
        first.ShouldContain("U10 Futbol");

        // Behind the cache's back, so only a cached copy can still show the old name.
        await using (var connection = new NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE groups.groups SET name = 'Yeni Ad' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", group);
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        (await Admin.GetStringAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct)).ShouldBe(first);

        await using var second = CreateApi();
        using var secondAdmin = second.SchoolAdminClient(School);
        (await secondAdmin.GetStringAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct)).ShouldBe(first);
    }

    [Fact]
    [Trait("Story", "P3-09")]
    public async Task A_second_instance_returns_the_same_reports_from_valkey()
    {
        // DEC-410: the cached facts (groups with names, counts per month, money totals) survive the round trip.
        var (student, group, enrollment) = await EnrolledAsync("U10 Futbol", fee: "1000.00");
        await RecordOkAsync(enrollment, "2027-01", "400.50", paidOn: "2027-01-10");
        await ScheduleAsync(await GetGroupAsync(group), Slot("Wednesday", "17:00", "18:00"));
        (await Admin.PutAsJsonAsync($"/api/v1/groups/{group}/sessions/2027-01-13T17:00/attendance", new { records = new[] { new { studentId = student, status = "Late" } } }, Ct))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        string[] urls = ["/api/v1/reports/attendance?by=coach", "/api/v1/reports/occupancy?by=sport", "/api/v1/reports/revenue"];

        var first = new List<string>();
        foreach (var url in urls)
        {
            first.Add(await Admin.GetStringAsync(new Uri(url, UriKind.Relative), Ct));
        }

        first[0].ShouldContain("\"late\":1");
        first[2].ShouldContain("599.50");

        await using (var connection = new NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE groups.groups SET capacity = 99 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", group);
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        await using var second = CreateApi();
        using var secondAdmin = second.SchoolAdminClient(School);
        for (var i = 0; i < urls.Length; i++)
        {
            (await secondAdmin.GetStringAsync(new Uri(urls[i], UriKind.Relative), Ct)).ShouldBe(first[i], urls[i]);
        }
    }
}
