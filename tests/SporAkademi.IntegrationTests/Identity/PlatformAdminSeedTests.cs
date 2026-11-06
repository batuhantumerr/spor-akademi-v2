using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Identity;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-01")]
public sealed class PlatformAdminSeedTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string SeedPassword = "Ilk-Platform-Sifresi-2027";

    private WebApplicationFactory<Program> WithSeedConfig(string? email, string? password = SeedPassword) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Config(email, password))));

    private static Dictionary<string, string?> Config(string? email, string? password) => new()
    {
        ["PlatformAdmin:Email"] = email,
        ["PlatformAdmin:InitialPassword"] = password,
    };

    /// <summary>A seed wired to this test's database, with a logger the test can read.</summary>
    private PlatformAdminSeed DirectSeed(string? email, string? password, RecordingLogger logger) =>
        new(
            Api.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(Config(email, password)).Build(),
            logger);

    private Task<List<User>> PlatformAdminsAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<User>().AsNoTracking()
            .Where(u => u.Role == UserRole.PlatformAdmin).ToListAsync());

    [Fact]
    public async Task Configured_platform_admin_is_created_once_and_must_change_the_password()
    {
        using (var first = WithSeedConfig("Platform@Example.com"))
        {
            _ = first.Services;
        }

        using (var second = WithSeedConfig("platform@example.com"))
        {
            _ = second.Services;
        }

        var admins = await PlatformAdminsAsync();
        admins.Count.ShouldBe(1);
        admins[0].Email.Value.ShouldBe("platform@example.com");
        admins[0].TenantId.ShouldBeNull();
        admins[0].MustChangePassword.ShouldBeTrue();

        var session = await LoginOkAsync("platform@example.com", SeedPassword);
        session.Body.User.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task An_existing_platform_admin_is_left_alone_even_with_another_configured_email()
    {
        var existing = await SeedUserAsync("existing@example.com", UserRole.PlatformAdmin, null);

        using (var api = WithSeedConfig("other@example.com"))
        {
            _ = api.Services;
        }

        var admins = await PlatformAdminsAsync();
        admins.Select(a => a.Id).ShouldBe([existing.Id]);
        (await ErrorCodeAsync(await LoginAsync("other@example.com", SeedPassword))).ShouldBe("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task Instances_starting_together_seed_one_platform_admin_and_all_start()
    {
        using var first = WithSeedConfig("first@example.com");
        using var second = WithSeedConfig("second@example.com");
        using var third = WithSeedConfig("first@example.com");

        await Task.WhenAll(
            Task.Run(() => first.Services, TestContext.Current.CancellationToken),
            Task.Run(() => second.Services, TestContext.Current.CancellationToken),
            Task.Run(() => third.Services, TestContext.Current.CancellationToken));

        (await PlatformAdminsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_seeds_create_exactly_one_admin()
    {
        var logger = new RecordingLogger();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            Task.Run(() => DirectSeed(null, null, logger).SeedAsync($"admin{i}@example.com", SeedPassword, CancellationToken.None))));

        outcomes.Count(o => o == PlatformAdminSeed.SeedOutcome.Created).ShouldBe(1);
        (await PlatformAdminsAsync()).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public async Task Without_configuration_nothing_happens(string? email, string? password)
    {
        var logger = new RecordingLogger();

        await DirectSeed(email, password, logger).StartAsync(CancellationToken.None);

        logger.Entries.ShouldBeEmpty();
        (await PlatformAdminsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("platform@example.com", null, "PlatformAdmin:InitialPassword")]
    [InlineData(null, SeedPassword, "PlatformAdmin:Email")]
    [InlineData("platform@example.com", "change-me", "PlatformAdmin:InitialPassword")]
    public async Task Partial_or_placeholder_configuration_warns_with_the_setting_name_only(string? email, string? password, string setting)
    {
        var logger = new RecordingLogger();

        await DirectSeed(email, password, logger).StartAsync(CancellationToken.None);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(setting);
        warning.Message.ShouldNotContain("platform@example.com");
        warning.Message.ShouldNotContain(SeedPassword);
        (await PlatformAdminsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_email_used_by_another_account_is_skipped_with_a_warning()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("platform@example.com", UserRole.Coach, school);
        var logger = new RecordingLogger();

        await DirectSeed("platform@example.com", SeedPassword, logger).StartAsync(CancellationToken.None);

        logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("PlatformAdmin:Email");
        (await PlatformAdminsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("platform@example.com", "1234567890")]
    [InlineData("not-an-email", SeedPassword)]
    public void Weak_password_or_invalid_email_stops_the_startup(string email, string password)
    {
        using var api = WithSeedConfig(email, password);

        Should.Throw<InvalidOperationException>(() => api.Services);
    }

    private sealed class RecordingLogger : ILogger<PlatformAdminSeed>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
