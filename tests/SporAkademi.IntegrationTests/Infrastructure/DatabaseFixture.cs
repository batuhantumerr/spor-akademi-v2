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
/// local bind mount), and migrations run as the migrator role (P1-08). The document storage container
/// (<see cref="StorageContainer"/>, DEC-320) starts alongside it and is shared the same way.
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

    public StorageContainer Storage { get; } = new(ReadImage(StorageImage()));

    public string AppConnectionString => Connection("spor_akademi_app", AppPassword);

    /// <summary>
    /// The migrator with the maintenance switch on (P4-04, DEC-522): its row level security policy then shows every
    /// school's rows, so tests can reset and inspect the database. The application role has no such policy.
    /// </summary>
    public string MigratorConnectionString => Connection("spor_akademi_migrator", MigratorPassword, "-c app.maintenance=on");

    /// <summary>The migrator without the maintenance switch, as a bare connection (P4-04).</summary>
    public string PlainMigratorConnectionString => Connection("spor_akademi_migrator", MigratorPassword);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), Storage.StartAsync().AsTask());

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
            // Hangfire's schema version row is part of the schema, like the migrations history (DEC-331).
            TablesToIgnore = [new Respawn.Graph.Table("hangfire", "schema")],
        });
    }

    /// <summary>Deletes all module data and Hangfire's jobs; schema, migrations history and Hangfire's schema version stay.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await Storage.DisposeAsync();
    }

    private string Connection(string user, string password, string? options = null) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = Database,
            Username = user,
            Password = password,
            Options = options,
        }.ConnectionString;

    /// <summary>compose.yaml is the single source of truth for the PostgreSQL image (tag + digest).</summary>
    private static string ReadPostgresImage() => ReadImage(PostgresImage());

    /// <summary>The image (tag + digest) of a compose.yaml service, so tests run exactly what development runs.</summary>
    private static string ReadImage(Regex pattern)
    {
        var compose = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "compose.yaml"));
        var match = pattern.Match(compose);
        return match.Success
            ? match.Groups["image"].Value
            : throw new InvalidOperationException($"compose.yaml has no image matching {pattern}.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>postgres:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex PostgresImage();

    [GeneratedRegex(@"^\s*image:\s*(?<image>chrislusf/seaweedfs:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex StorageImage();
}

[CollectionDefinition(Name)]
public sealed class DatabaseTestGroup : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
