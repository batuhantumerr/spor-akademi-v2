using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Organization;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Tenancy;

/// <summary>ADR-0003 layers 1–2 and US-02, exercised through the first tenant-scoped entity.</summary>
[Trait("Story", "US-02")]
public sealed class TenantIsolationTests(DatabaseFixture database) : IntegrationTest(database)
{
    [Fact]
    public async Task A_school_only_sees_its_own_records()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));
        using var adminA = Api.SchoolAdminClient(schoolA);
        using var adminB = Api.SchoolAdminClient(schoolB);
        await adminA.PostAsJsonAsync("/api/v1/sports", new { name = "Futbol" }, Ct);
        await adminB.PostAsJsonAsync("/api/v1/sports", new { name = "Yüzme" }, Ct);

        var listA = await adminA.GetFromJsonAsync<JsonElement>("/api/v1/sports", Ct);

        listA.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ShouldBe(["Futbol"]);
    }

    [Fact]
    public async Task Another_schools_record_is_not_found_for_read_write_and_delete()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));
        using var adminA = Api.SchoolAdminClient(schoolA);
        using var adminB = Api.SchoolAdminClient(schoolB);
        var sportOfB = await (await adminB.PostAsJsonAsync("/api/v1/sports", new { name = "Yüzme" }, Ct)).Content.ReadFromJsonAsync<SportDto>(Ct);

        (await adminA.GetAsync(new Uri($"/api/v1/sports/{sportOfB!.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PutAsJsonAsync($"/api/v1/sports/{sportOfB.Id}", new { name = "Hack", version = sportOfB.Version }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.PostAsync(new Uri($"/api/v1/sports/{sportOfB.Id}/archive", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminA.DeleteAsync(new Uri($"/api/v1/sports/{sportOfB.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await adminB.GetFromJsonAsync<SportDto>($"/api/v1/sports/{sportOfB.Id}", Ct))!.Name.ShouldBe("Yüzme");
    }

    [Fact]
    public async Task Without_a_tenant_queries_return_nothing()
    {
        var school = await TestSchools.CreateAsync(Api, "okul-a");
        using var admin = Api.SchoolAdminClient(school);
        await admin.PostAsJsonAsync("/api/v1/sports", new { name = "Futbol" }, Ct);

        // Anonymous scope (no HTTP user): the filter must deny, not allow everything.
        var count = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Sport>().CountAsync(Ct));

        count.ShouldBe(0);
    }

    [Fact]
    public async Task Filter_follows_each_context_not_the_first_one_built()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));
        using var adminA = Api.SchoolAdminClient(schoolA);
        using var adminB = Api.SchoolAdminClient(schoolB);
        await adminA.PostAsJsonAsync("/api/v1/sports", new { name = "A1" }, Ct);
        await adminB.PostAsJsonAsync("/api/v1/sports", new { name = "B1" }, Ct);
        await adminB.PostAsJsonAsync("/api/v1/sports", new { name = "B2" }, Ct);

        // Interleave requests of both schools on the same cached model.
        for (var i = 0; i < 3; i++)
        {
            (await adminA.GetFromJsonAsync<JsonElement>("/api/v1/sports", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
            (await adminB.GetFromJsonAsync<JsonElement>("/api/v1/sports", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(2);
        }
    }

    [Fact]
    public async Task Writing_another_schools_row_is_blocked_by_the_write_guard()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));

        await using var scope = Api.Services.CreateAsyncScope();
        var db = ActingAs(scope.ServiceProvider, schoolA);
        db.Add(Sport.Create(schoolB, "Sızma").Value);

        await Should.ThrowAsync<TenantIsolationException>(() => db.SaveChangesAsync(Ct));
    }

    private static AppDbContext ActingAs(IServiceProvider services, SchoolId school)
    {
        // A context whose current user is a School Admin of `school`, wired like the real one.
        var user = new FixedUser(school.Value);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(services.GetRequiredService<AppDbContext>().Database.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantWriteGuardInterceptor(user))
            .Options;
        return new AppDbContext(options, user);
    }

    private sealed class FixedUser(Guid tenant) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.CreateVersion7();

        public Guid? TenantId => tenant;

        public Role? Role => Application.Abstractions.Authentication.Role.SchoolAdmin;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
