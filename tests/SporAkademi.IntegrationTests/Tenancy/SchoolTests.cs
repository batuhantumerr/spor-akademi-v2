using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Tenancy;

[Trait("Story", "US-01")]
public sealed class SchoolTests(DatabaseFixture database) : IdentityTest(database)
{
    private string PlatformToken => TestTokens.Create(Guid.CreateVersion7(), "PlatformAdmin", null, Api.Clock.GetUtcNow());

    private static object NewSchool(string slug = "moda-spor", string email = "mudur@example.com", string timeZone = "Europe/Istanbul") => new
    {
        name = "Moda Spor Kulübü",
        slug,
        timeZone,
        admin = new { firstName = "Selin", lastName = "Aksoy", email },
    };

    private Task<HttpResponseMessage> CreateAsync(object body, string? token = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/platform/schools", token ?? PlatformToken, body);

    private Task<(int Schools, int Users)> CountAsync() =>
        InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            return (await db.Set<School>().CountAsync(), await db.Set<User>().CountAsync());
        });

    [Fact]
    public async Task Platform_admin_creates_an_active_school_with_its_first_admin()
    {
        var response = await CreateAsync(NewSchool());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var school = body.GetProperty("school");
        var schoolId = school.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/platform/schools/{schoolId}");
        school.GetProperty("name").GetString().ShouldBe("Moda Spor Kulübü");
        school.GetProperty("slug").GetString().ShouldBe("moda-spor");
        school.GetProperty("timeZone").GetString().ShouldBe("Europe/Istanbul");
        school.GetProperty("status").GetString().ShouldBe("Active");

        // The first admin signs in with the temporary password and must change it (US-05).
        var temporary = body.GetProperty("temporaryPassword").GetString()!;
        var session = await LoginOkAsync("mudur@example.com", temporary);
        session.Body.User.Role.ShouldBe("SchoolAdmin");
        session.Body.User.TenantId.ShouldBe(schoolId);
        session.Body.User.Id.ShouldBe(body.GetProperty("adminId").GetGuid());
        session.Body.User.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task Taken_slug_is_rejected_and_no_admin_is_created()
    {
        (await CreateAsync(NewSchool())).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await CreateAsync(NewSchool(email: "baska@example.com"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("School.SlugTaken");
        (await CountAsync()).ShouldBe((1, 1));
    }

    [Fact]
    public async Task Taken_admin_email_is_rejected_and_no_school_is_created()
    {
        var existing = await SeedSchoolAsync("eski-okul");
        await SeedUserAsync("mudur@example.com", UserRole.Coach, existing);

        var response = await CreateAsync(NewSchool(email: "MUDUR@example.com"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("User.EmailTaken");
        (await CountAsync()).ShouldBe((1, 1));
    }

    [Theory]
    [InlineData("Mars/Olympus", "moda-spor", "mudur@example.com", "timeZone", "School.InvalidTimeZone")]
    [InlineData("Europe/Istanbul", "Moda Spor", "mudur@example.com", "slug", "Slug.Invalid")]
    [InlineData("Europe/Istanbul", "moda-spor", "not-an-email", "admin.email", "Email.Invalid")]
    [InlineData("Europe/Istanbul", "", "mudur@example.com", "slug", "Field.Required")]
    public async Task Invalid_input_is_a_validation_error_and_creates_nothing(string timeZone, string slug, string email, string field, string code)
    {
        var response = await CreateAsync(NewSchool(slug, email, timeZone));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Validation.Failed");
        problem.GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(code);
        (await CountAsync()).ShouldBe((0, 0));
    }

    [Theory]
    [InlineData("SchoolAdmin")]
    [InlineData("Coach")]
    public async Task Only_the_platform_admin_creates_or_lists_schools(string role)
    {
        var school = await SeedSchoolAsync();
        var token = TestTokens.Create(Guid.CreateVersion7(), role, school.Value, Api.Clock.GetUtcNow());

        (await CreateAsync(NewSchool(), token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/platform/schools", token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountAsync()).ShouldBe((1, 0));
    }

    [Fact]
    public async Task Schools_are_listed_by_name_with_status_and_first_admin()
    {
        await CreateAsync(new { name = "Zeytinburnu Spor", slug = "zeytinburnu", timeZone = "Europe/Istanbul", admin = new { firstName = "Zeki", lastName = "Z", email = "z@example.com" } });
        await CreateAsync(new { name = "Ataşehir Spor", slug = "atasehir", timeZone = "Europe/Istanbul", admin = new { firstName = "Ayla", lastName = "A", email = "a@example.com" } });

        var response = await SendAsync(HttpMethod.Get, "/api/v1/platform/schools?page=1&pageSize=1", PlatformToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        page.GetProperty("totalCount").GetInt32().ShouldBe(2);
        page.GetProperty("totalPages").GetInt32().ShouldBe(2);
        var first = page.GetProperty("items")[0];
        first.GetProperty("name").GetString().ShouldBe("Ataşehir Spor");
        first.GetProperty("status").GetString().ShouldBe("Active");
        first.GetProperty("admin").GetProperty("email").GetString().ShouldBe("a@example.com");
        first.GetProperty("admin").GetProperty("status").GetString().ShouldBe("Active");
    }

    [Fact]
    public async Task Page_size_is_capped_at_100()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/v1/platform/schools?pageSize=101", PlatformToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
