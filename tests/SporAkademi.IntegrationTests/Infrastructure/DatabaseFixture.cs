using System.Text.RegularExpressions;
using DotNet.Testcontainers.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using SporAkademi.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test run, built exactly like local development: the image comes from
/// compose.yaml, roles from the same init script (executable, so it runs as a subprocess like the
/// local bind mount), and migrations run as the migrator role (P1-08).
/// </summary>
public sealed partial class DatabaseFixture : IAsyncLifetime
{
    private const string Database = "spor_akademi";
    private const string MigratorPassword = "migratorTestPassword1";
    private const string AppPassword = "appTestPassword1";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(ReadPostgresImage())
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword("postgresTestPassword1")
        .WithEnvironment("SPOR_AKADEMI_DB", Database)
        .WithEnvironment("SPOR_AKADEMI_MIGRATOR_PASSWORD", MigratorPassword)
        .WithEnvironment("SPOR_AKADEMI_APP_PASSWORD", AppPassword)
        .WithResourceMapping(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "10-create-roles.sh")), "/docker-entrypoint-initdb.d/10-create-roles.sh", fileMode: Unix.FileMode755)
        .Build();

    private Respawner? _respawner;

    public string AppConnectionString => Connection("spor_akademi_app", AppPassword);

    public string MigratorConnectionString => Connection("spor_akademi_migrator", MigratorPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(MigratorConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var context = new AppDbContext(options, new AnonymousCurrentUser()))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToExclude = ["public"],
        });
    }

    /// <summary>Deletes all module data; schema and migrations history stay.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private string Connection(string user, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = Database,
            Username = user,
            Password = password,
        }.ConnectionString;

    /// <summary>compose.yaml is the single source of truth for the PostgreSQL image (tag + digest).</summary>
    private static string ReadPostgresImage()
    {
        var compose = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "compose.yaml"));
        var match = PostgresImage().Match(compose);
        return match.Success
            ? match.Groups["image"].Value
            : throw new InvalidOperationException("compose.yaml has no postgres image.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>postgres:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex PostgresImage();
}

[CollectionDefinition(Name)]
public sealed class DatabaseTestGroup : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
