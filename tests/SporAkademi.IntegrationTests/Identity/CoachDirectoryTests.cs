using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Identity;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-08")]
public sealed class CoachDirectoryTests(DatabaseFixture database) : IdentityTest(database)
{
    private Task<T> AsUserOfAsync<T>(SchoolId? school, Func<ICoachDirectory, Task<T>> action) =>
        InScopeAsync(sp => action(new CoachDirectory(sp.GetRequiredService<AppDbContext>(), new FixedUser(school))));

    [Fact]
    public async Task Only_coaches_of_the_current_school_are_found()
    {
        var school = await SeedSchoolAsync();
        var other = await SeedSchoolAsync("baska-okul");
        var active = await SeedUserAsync("active@example.com", UserRole.Coach, school);
        var inactive = await SeedUserAsync("inactive@example.com", UserRole.Coach, school);
        await DeactivateUserAsync(inactive.Id);
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var foreign = await SeedUserAsync("foreign@example.com", UserRole.Coach, other);
        var unknown = Guid.CreateVersion7();

        var many = await AsUserOfAsync(school, d => d.GetManyAsync([active.Id.Value, inactive.Id.Value, admin.Id.Value, foreign.Id.Value, unknown], CancellationToken.None));

        many.Keys.Order().ShouldBe(new[] { active.Id.Value, inactive.Id.Value }.Order());
        many[active.Id.Value].ShouldBe(new CoachInfo(active.Id.Value, "Deniz", "Kaya", IsActive: true));
        many[inactive.Id.Value].IsActive.ShouldBeFalse();

        (await AsUserOfAsync(school, d => d.FindAsync(active.Id.Value, CancellationToken.None)))!.IsActive.ShouldBeTrue();
        (await AsUserOfAsync(school, d => d.FindAsync(admin.Id.Value, CancellationToken.None))).ShouldBeNull();
        (await AsUserOfAsync(school, d => d.FindAsync(foreign.Id.Value, CancellationToken.None))).ShouldBeNull();
        (await AsUserOfAsync(other, d => d.FindAsync(active.Id.Value, CancellationToken.None))).ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_school_nothing_is_found()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        (await AsUserOfAsync(null, d => d.GetManyAsync([coach.Id.Value], CancellationToken.None))).ShouldBeEmpty();
        (await AsUserOfAsync(null, d => d.FindAsync(coach.Id.Value, CancellationToken.None))).ShouldBeNull();
    }

    [Fact]
    public void It_is_the_registered_implementation()
    {
        using var scope = Api.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICoachDirectory>().ShouldBeOfType<CoachDirectory>();
    }

    private sealed class FixedUser(SchoolId? school) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.CreateVersion7();

        public Guid? TenantId => school?.Value;

        public Role? Role => school is null ? Application.Abstractions.Authentication.Role.PlatformAdmin : Application.Abstractions.Authentication.Role.SchoolAdmin;
    }
}
