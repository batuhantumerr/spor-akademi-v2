using Npgsql;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Jobs;

/// <summary>DEC-331: the migrator creates Hangfire's schema; the app role uses it without any DDL right.</summary>
[Trait("Story", "P2-F2")]
public sealed class HangfireSchemaTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_migrations_install_the_schema_version_the_package_ships()
    {
        var installed = await ScalarAsync<int>("SELECT version FROM hangfire.schema");

        installed.ShouldBe(HangfireSchema.InstalledVersion);
        // Fails after a Hangfire.PostgreSql upgrade with new scripts until a migration installs them.
        HangfireSchema.PackagedVersion().ShouldBe(HangfireSchema.InstalledVersion);
    }

    [Fact]
    public async Task The_schema_belongs_to_the_migrator_and_the_app_role_only_reads_and_writes_rows()
    {
        (await ScalarAsync<string>("SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'hangfire'")).ShouldBe("spor_akademi_migrator");
        (await ScalarAsync<bool>("SELECT has_schema_privilege('spor_akademi_app', 'hangfire', 'CREATE')")).ShouldBeFalse();
        (await ScalarAsync<bool>("SELECT has_schema_privilege('spor_akademi_app', 'hangfire', 'USAGE')")).ShouldBeTrue();
        (await ScalarAsync<bool>("SELECT has_table_privilege('spor_akademi_app', 'hangfire.job', 'SELECT, INSERT, UPDATE, DELETE')")).ShouldBeTrue();
        (await ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'hangfire' AND tableowner <> 'spor_akademi_migrator'")).ShouldBe(0);

        // And the app role cannot create anything there.
        await using var app = new NpgsqlConnection(Database.AppConnectionString);
        await app.OpenAsync(Ct);
        await using var create = new NpgsqlCommand("CREATE TABLE hangfire.intruder (id int)", app);
        var error = await Should.ThrowAsync<PostgresException>(() => create.ExecuteNonQueryAsync(Ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
