using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// Used by dotnet-ef (through scripts/ef.sh) and by the migrations bundle of the migrator image. The connection string
/// comes from an environment variable: <c>SPOR_AKADEMI_DESIGN_CONNECTION</c>, which ef.sh sets for the migrator role, or
/// <c>ConnectionStrings__Migrator</c>, which a container platform fills from a secret (P5-06, DEC-653; the chiseled image
/// has no shell to build a <c>--connection</c> argument from one). Commands that do not touch the database (migrations
/// add/script) work without either.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string PlaceholderConnection = "Host=localhost;Database=spor_akademi_design_time";

    internal static string ResolveConnection(Func<string, string?> environment) =>
        FirstSet(environment("SPOR_AKADEMI_DESIGN_CONNECTION")) ?? FirstSet(environment("ConnectionStrings__Migrator")) ?? PlaceholderConnection;

    private static string? FirstSet(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = ResolveConnection(Environment.GetEnvironmentVariable);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new AnonymousCurrentUser());
    }
}
