using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>The real API wired to the test database and document store, with a controllable clock.</summary>
/// <param name="storage">The <c>Storage</c> settings; without them the store is unconfigured (readiness reports Degraded).</param>
public sealed class ApiFactory(string appConnectionString, IReadOnlyDictionary<string, string?>? storage = null) : WebApplicationFactory<Program>
{
    public const string JwtSigningKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Defaults to 2027-01-14 12:00 UTC (a Thursday) so date rules are deterministic.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = appConnectionString,
                ["Jwt:SigningKey"] = JwtSigningKey,
                ["Seq:ServerUrl"] = null,
                ["Serilog:MinimumLevel:Default"] = "Fatal",
                // Tests drive OutboxProcessor themselves so delivery happens exactly when they decide (OutboxWorkerTests turn it on).
                ["Outbox:Enabled"] = "false",
            })
            .AddInMemoryCollection(storage ?? new Dictionary<string, string?>()));
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }
}
