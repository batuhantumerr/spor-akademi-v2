using Npgsql;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Caching;

/// <summary>
/// DEC-314: the dashboard survives the System.Text.Json round trip through Valkey unchanged (money as text, decimal
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
}
