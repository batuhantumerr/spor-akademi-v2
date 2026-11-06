using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SporAkademi.DemoSeed;

/// <summary>
/// P1-38: <c>bash scripts/seed-demo.sh</c> (or <c>dotnet run --project src/SporAkademi.DemoSeed</c>), DEC-220.
/// An explicit entry class: a top-level <c>Program</c> would clash with the API's in the integration tests.
/// </summary>
internal static class DemoSeedProgram
{
    public const int Refused = 3;
    public const int WrongEnvironment = 2;

    public static async Task<int> Main(string[] args)
    {
        using var host = DemoSeedHost.Create(args);
        return await RunAsync(host, new DemoSeedOptions(TimeProvider.System.GetUtcNow(), Password: Environment.GetEnvironmentVariable("DEMO_PASSWORD")));
    }

    public static async Task<int> RunAsync(IHost host, DemoSeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        // Synthetic data only, and never into production: an unset environment counts as Production (fail closed).
        if (host.Services.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            await Console.Error.WriteLineAsync("Refusing to seed demo data in the Production environment. Set DOTNET_ENVIRONMENT=Development.");
            return WrongEnvironment;
        }

        var configuredPassword = options.Password;
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(options, CancellationToken.None);
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

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            // Shown once; it is stored only as a hash.
            Console.WriteLine($"  Password of every demo account (shown only now): {result.Password}");
        }

        return 0;
    }
}
