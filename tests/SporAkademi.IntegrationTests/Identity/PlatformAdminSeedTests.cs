using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-01")]
public sealed class PlatformAdminSeedTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string SeedPassword = "Ilk-Platform-Sifresi-2027";

    private WebApplicationFactory<Program> WithSeedConfig(string? email, string? password = SeedPassword) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlatformAdmin:Email"] = email,
            ["PlatformAdmin:InitialPassword"] = password,
        })));

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

        using (var second = WithSeedConfig("other@example.com"))
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

    [Theory]
    [InlineData(null, SeedPassword)]
    [InlineData("platform@example.com", null)]
    [InlineData("", "")]
    public async Task Without_configuration_nothing_is_seeded(string? email, string? password)
    {
        using (var api = WithSeedConfig(email, password))
        {
            _ = api.Services;
        }

        (await PlatformAdminsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public void A_weak_initial_password_stops_the_startup()
    {
        using var api = WithSeedConfig("platform@example.com", "1234567890");

        Should.Throw<InvalidOperationException>(() => api.Services);
    }
}
