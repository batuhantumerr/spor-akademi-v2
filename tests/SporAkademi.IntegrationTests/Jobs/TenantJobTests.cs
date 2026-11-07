using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Jobs;

/// <summary>DEC-334: jobs reach school data only through <see cref="TenantJobRunner"/>, one school per scope.</summary>
[Trait("Story", "P2-F2")]
public sealed class TenantJobTests(DatabaseFixture database) : IdentityTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TenantJobRunner Runner => Api.Services.GetRequiredService<TenantJobRunner>();

    [Fact]
    public async Task Each_school_runs_in_its_own_scope_and_sees_only_its_own_rows()
    {
        var (a, b) = (await SeedSchoolAsync("okul-a"), await SeedSchoolAsync("okul-b"));
        await SeedSportsAsync((a, "Yüzme"), (a, "Tenis"), (b, "Basketbol"));
        var seen = new ConcurrentDictionary<SchoolId, (Guid? Tenant, string[] Sports)>();
        var contexts = new ConcurrentBag<AppDbContext>();

        var count = await Runner.ForEachSchoolAsync("test", async (services, school, ct) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            contexts.Add(db);
            var sports = await db.Set<Sport>().Select(s => s.Name).OrderBy(n => n).ToArrayAsync(ct);
            seen[school] = (services.GetRequiredService<ICurrentUser>().TenantId, sports);
        }, Ct);

        count.ShouldBe(2);
        seen[a].Tenant.ShouldBe(a.Value);
        seen[a].Sports.ShouldBe(["Tenis", "Yüzme"]);
        seen[b].Tenant.ShouldBe(b.Value);
        seen[b].Sports.ShouldBe(["Basketbol"]);
        contexts.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_write_into_another_school_fails_that_school_only_and_then_the_run()
    {
        var (a, b) = (await SeedSchoolAsync("okul-a"), await SeedSchoolAsync("okul-b"));
        var ran = new ConcurrentBag<SchoolId>();

        var run = Runner.ForEachSchoolAsync("test", async (services, school, ct) =>
        {
            ran.Add(school);
            var db = services.GetRequiredService<AppDbContext>();
            // School A's run tries to write a sport of school B: the write guard refuses it.
            db.Add(Sport.Create(school == a ? b : school, $"Spor {ran.Count}").Value);
            await db.SaveChangesAsync(ct);
        }, Ct);

        await Should.ThrowAsync<TenantJobException>(run);
        ran.ShouldBe([a, b], ignoreOrder: true);
        var sports = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Sport>().IgnoreQueryFilters().ToListAsync(Ct));
        sports.ShouldHaveSingleItem().TenantId.ShouldBe(b);
    }

    [Fact]
    public async Task The_refresh_token_cleanup_deletes_tokens_of_ended_sessions_in_every_school_and_of_platform_admins()
    {
        var (a, b) = (await SeedSchoolAsync("okul-a"), await SeedSchoolAsync("okul-b"));
        var adminA = await SeedUserAsync("admin-a@example.com", UserRole.SchoolAdmin, a);
        var coachB = await SeedUserAsync("coach-b@example.com", UserRole.Coach, b);
        var platform = await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        await LoginOkAsync("admin-a@example.com");
        await LoginOkAsync("coach-b@example.com");
        await LoginOkAsync("platform@example.com");

        // Every session (30 days) ends; then school A's admin starts a new one.
        Api.Clock.Advance(User.SessionLifetime + TimeSpan.FromMinutes(1));
        await LoginOkAsync("admin-a@example.com");
        await using var scope = Api.Services.CreateAsyncScope();

        var deleted = await scope.ServiceProvider.GetRequiredService<RefreshTokenCleanupJob>().RunAsync(Ct);

        // Two each (seeded session and login) for the coach and the Platform Admin; school A's admin dropped its own at login.
        deleted.ShouldBe(4);
        (await LoadUserAsync(adminA.Id)).RefreshTokens.ShouldHaveSingleItem().FamilyExpiresAt.ShouldBeGreaterThan(Api.Clock.GetUtcNow());
        (await LoadUserAsync(coachB.Id)).RefreshTokens.ShouldBeEmpty();
        (await LoadUserAsync(platform.Id)).RefreshTokens.ShouldBeEmpty();
    }

    private Task SeedSportsAsync(params (SchoolId School, string Name)[] sports) =>
        InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.AddRange(sports.Select(s => Sport.Create(s.School, s.Name).Value));
            await db.SaveChangesAsync(Ct);
        });
}
