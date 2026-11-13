using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace SporAkademi.DemoSeed;

/// <summary>
/// P1-38: <c>bash scripts/seed-demo.sh</c> (or <c>dotnet run --project src/SporAkademi.DemoSeed</c>), DEC-220.
/// An explicit entry class: a top-level <c>Program</c> would clash with the API's in the integration tests.
/// </summary>
internal static class DemoSeedProgram
{
    public const int WrongEnvironment = 2;
    public const int Refused = 3;
    public const int NotLocalDatabase = 4;
    public const int Failed = 5;

    public const string ResetHint =
        "Partial demo data may remain. To start over with an empty local database: "
        + "docker compose down -v && docker compose up -d --wait && bash scripts/ef.sh database update "
        + "(this deletes ALL local data), then run scripts/seed-demo.sh again.";

    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    /// <summary>True when the connection string names a database server on this machine only.</summary>
    public static bool IsLocalDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var hosts = new NpgsqlConnectionStringBuilder(connectionString).Host?
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(HostWithoutPort)
                .ToList();
            return hosts is { Count: > 0 } && hosts.All(h => LocalHosts.Contains(h, StringComparer.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary><c>host:port</c> → host; <c>[::1]:5432</c> → <c>[::1]</c>; a bare IPv6 literal stays as it is.</summary>
    private static string HostWithoutPort(string host) =>
        host.StartsWith('[') ? host[..(host.IndexOf(']', StringComparison.Ordinal) + 1)]
        : host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':', StringComparison.Ordinal)]
        : host;

    public static async Task<int> Main(string[] args)
    {
        using var host = DemoSeedHost.Create(args);
        // P4-08 (DEC-562): the load test asks for extra schools, parent accounts and a manifest of the sign-in addresses
        // (DemoSeed:LoadSchools, DemoSeed:ParentAccounts, DemoSeed:Manifest); by default nothing changes.
        var settings = host.Services.GetRequiredService<IConfiguration>().GetSection("DemoSeed");
        var options = new DemoSeedOptions(
            TimeProvider.System.GetUtcNow(),
            Password: Environment.GetEnvironmentVariable("DEMO_PASSWORD"),
            LoadSchools: settings.GetValue("LoadSchools", 0),
            ParentAccounts: settings.GetValue("ParentAccounts", 0));
        return await RunAsync(host, options, settings["Manifest"]);
    }

    public static async Task<int> RunAsync(IHost host, DemoSeedOptions options, string? manifestPath = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        // Synthetic data only, and never into production: an unset environment counts as Production (fail closed).
        if (host.Services.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            await Console.Error.WriteLineAsync("Refusing to seed demo data in the Production environment. Set DOTNET_ENVIRONMENT=Development.");
            return WrongEnvironment;
        }

        // Second guard: only a database on this machine, whatever the environment says.
        var connection = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("App");
        if (!IsLocalDatabase(connection))
        {
            await Console.Error.WriteLineAsync("Refusing to seed demo data: the 'App' connection string must point to localhost, 127.0.0.1 or ::1.");
            return NotLocalDatabase;
        }

        var configuredPassword = options.Password;
        await using var scope = host.Services.CreateAsyncScope();
        DemoSeedResult result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(options, CancellationToken.None);
        }
        catch (Exception exception) when (exception is DemoSeedException or DbException or InvalidOperationException)
        {
            // Each command commits on its own, so a failure leaves partial demo data behind.
            await Console.Error.WriteLineAsync($"Demo seeding failed: {exception.Message}");
            await Console.Error.WriteLineAsync(ResetHint);
            return Failed;
        }

        if (!result.Created)
        {
            await Console.Error.WriteLineAsync("Demo schools already exist; nothing was changed.");
            return Refused;
        }

        Console.WriteLine("Demo data created:");
        foreach (var school in result.Schools)
        {
            Console.WriteLine($"  {school.Name} ({school.Slug}): School Admin {school.AdminEmail}, coaches antrenor1..n@{school.Slug}.test");
        }

        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            await WriteManifestAsync(manifestPath, result.Schools);
            Console.WriteLine($"  Sign-in addresses (no passwords) written to {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            // Shown once; it is stored only as a hash.
            Console.WriteLine($"  Password of every demo account (shown only now): {result.Password}");
        }

        return 0;
    }

    /// <summary>
    /// P4-08: the k6 scenarios read who signs in where (perf/k6). Synthetic <c>.test</c> addresses only; the password is
    /// never written (the load test gets DEMO_PASSWORD from its environment).
    /// </summary>
    internal static async Task WriteManifestAsync(string path, IReadOnlyList<DemoSchoolSummary> schools)
    {
        var manifest = new
        {
            schools = schools.Select(s => new { slug = s.Slug, admin = s.AdminEmail, coaches = s.CoachEmails, parents = s.ParentEmails }),
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ManifestJson));
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };
}
