using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using SporAkademi.Application.Abstractions.Notifications;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>
/// The real API wired to the test database, with a controllable clock. Without <paramref name="cacheConnectionString"/>
/// the cache is in memory only (DEC-315); without <paramref name="storage"/> (the <c>Storage</c> settings) the document
/// store is unconfigured and readiness reports Degraded (DEC-326).
/// </summary>
public sealed class ApiFactory(
    string appConnectionString,
    string? cacheConnectionString = null,
    IReadOnlyDictionary<string, string?>? storage = null) : WebApplicationFactory<Program>
{
    public const string JwtSigningKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Defaults to 2027-01-14 12:00 UTC (a Thursday) so date rules are deterministic.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Every e-mail the API "sends" (DEC-350); nothing leaves the test.</summary>
    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = appConnectionString,
                ["ConnectionStrings:Cache"] = cacheConnectionString,
                ["Jwt:SigningKey"] = JwtSigningKey,
                ["Seq:ServerUrl"] = null,
                ["Serilog:MinimumLevel:Default"] = "Fatal",
                // Tests drive OutboxProcessor themselves so delivery happens exactly when they decide (OutboxWorkerTests and
                // the job tests turn a scheduler on), and run no Hangfire server unless a test asks for one.
                ["Outbox:Scheduler"] = "None",
                ["Jobs:ServerEnabled"] = "false",
                // Fee reminders are off by default (DEC-355); the tests turn them on from before the test clock's dates.
                ["Billing:DuesReminders:Enabled"] = "true",
                ["Billing:DuesReminders:StartsOn"] = "2026-01-01",
                // P4-02: every test client is one address and few users; the general API limits are lifted so that only
                // ApiRateLimitingTests (which sets small ones) meets them. The per-endpoint limits stay as in production.
                ["RateLimiting:UserPermitsPerMinute"] = "1000000",
                ["RateLimiting:SchoolWritePermitsPerMinute"] = "1000000",
                ["RateLimiting:AnonymousPermitsPerMinute"] = "1000000",
            })
            .AddInMemoryCollection(storage ?? new Dictionary<string, string?>()));
        builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
            services.Replace(ServiceDescriptor.Singleton<IEmailSender>(Emails));
        });
    }
}
