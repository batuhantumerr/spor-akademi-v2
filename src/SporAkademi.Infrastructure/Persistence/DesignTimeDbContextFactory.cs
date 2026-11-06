using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// Used only by dotnet-ef (through scripts/ef.sh). The connection string comes from an
/// environment variable that ef.sh sets for the migrator role; commands that do not touch
/// the database (migrations add/script) work without it.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string PlaceholderConnection = "Host=localhost;Database=spor_akademi_design_time";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SPOR_AKADEMI_DESIGN_CONNECTION")
            ?? PlaceholderConnection;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
