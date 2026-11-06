using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-08")]
public sealed class CoachTests(DatabaseFixture database) : IdentityTest(database)
{
    private SchoolId _school;
    private string _adminToken = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _school = await SeedSchoolAsync();
        _adminToken = TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", _school.Value, Api.Clock.GetUtcNow());
    }

    private Task<HttpResponseMessage> CreateCoachAsync(string email = "ayse@example.com", string? phone = "0532 123 45 67", string lastName = "Yılmaz", string? token = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/coaches", token ?? _adminToken, new { firstName = "Ayşe", lastName, email, phone });

    private async Task<(Guid Id, string TemporaryPassword)> CreatedCoachAsync(string email = "ayse@example.com", string lastName = "Yılmaz")
    {
        var response = await CreateCoachAsync(email, lastName: lastName);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return (body.GetProperty("coach").GetProperty("id").GetGuid(), body.GetProperty("temporaryPassword").GetString()!);
    }

    private async Task<JsonElement> GetCoachAsync(Guid id) =>
        await (await SendAsync(HttpMethod.Get, $"/api/v1/coaches/{id}", _adminToken)).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Creating_a_coach_opens_a_coach_account_with_a_temporary_password_in_my_school()
    {
        var response = await CreateCoachAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var coach = body.GetProperty("coach");
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/coaches/{coach.GetProperty("id").GetGuid()}");
        coach.GetProperty("firstName").GetString().ShouldBe("Ayşe");
        coach.GetProperty("email").GetString().ShouldBe("ayse@example.com");
        coach.GetProperty("phone").GetString().ShouldBe("+905321234567");
        coach.GetProperty("status").GetString().ShouldBe("Active");

        var session = await LoginOkAsync("ayse@example.com", body.GetProperty("temporaryPassword").GetString()!);
        session.Body.User.Role.ShouldBe("Coach");
        session.Body.User.TenantId.ShouldBe(_school.Value);
        session.Body.User.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task Email_must_be_unique_across_the_platform()
    {
        var other = await SeedSchoolAsync("baska-okul");
        await SeedUserAsync("ayse@example.com", UserRole.SchoolAdmin, other);

        var response = await CreateCoachAsync("AYSE@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("User.EmailTaken");
    }

    [Theory]
    [InlineData("not-an-email", "0532 123 45 67", "email", "Email.Invalid")]
    [InlineData("ayse@example.com", "12345", "phone", "PhoneNumber.Invalid")]
    [InlineData("", null, "email", "Field.Required")]
    public async Task Invalid_input_is_a_validation_error(string email, string? phone, string field, string code)
    {
        var response = await CreateCoachAsync(email, phone);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Deactivated_coach_cannot_log_in_and_loses_all_refresh_tokens()
    {
        var (id, temporary) = await CreatedCoachAsync();
        var session = await LoginOkAsync("ayse@example.com", temporary);

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{id}/deactivate", _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("status").GetString().ShouldBe("Deactivated");
        (await ErrorCodeAsync(await LoginAsync("ayse@example.com", temporary))).ShouldBe("Auth.AccountInactive");
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoadUserAsync(Domain.Identity.UserId.From(id))).RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);

        var again = await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{id}/deactivate", _adminToken);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(again)).ShouldBe("User.AlreadyInactive");
    }

    [Fact]
    public async Task Groups_can_ask_which_coaches_are_still_active()
    {
        var (active, _) = await CreatedCoachAsync("a@example.com");
        var (inactive, _) = await CreatedCoachAsync("b@example.com");
        await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{inactive}/deactivate", _adminToken);
        var other = await SeedSchoolAsync("baska-okul");

        var result = await InScopeAsync(sp => sp.GetRequiredService<IIdentityModule>()
            .GetActiveCoachIdsAsync(_school, [active, inactive, Guid.CreateVersion7()], CancellationToken.None));
        var fromOtherSchool = await InScopeAsync(sp => sp.GetRequiredService<IIdentityModule>()
            .GetActiveCoachIdsAsync(other, [active], CancellationToken.None));

        result.ShouldBe([active]);
        fromOtherSchool.ShouldBeEmpty();
    }

    [Fact]
    public async Task Name_and_phone_are_updated_with_the_version_read()
    {
        var (id, _) = await CreatedCoachAsync();
        var coach = await GetCoachAsync(id);
        var version = coach.GetProperty("version").GetUInt32();

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/coaches/{id}", _adminToken, new { firstName = "Ayşegül", lastName = "Yılmaz", phone = (string?)null, version });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        updated.GetProperty("firstName").GetString().ShouldBe("Ayşegül");
        updated.GetProperty("phone").ValueKind.ShouldBe(JsonValueKind.Null);
        updated.GetProperty("email").GetString().ShouldBe("ayse@example.com");
        updated.GetProperty("version").GetUInt32().ShouldNotBe(version);

        // A stale version means someone else changed it meanwhile.
        var stale = await SendAsync(HttpMethod.Put, $"/api/v1/coaches/{id}", _adminToken, new { firstName = "Ayşe", lastName = "Yılmaz", phone = (string?)null, version });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(stale)).ShouldBe("Concurrency.Conflict");
    }

    [Fact]
    public async Task Deactivated_coaches_cannot_be_edited()
    {
        var (id, _) = await CreatedCoachAsync();
        await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{id}/deactivate", _adminToken);
        var version = (await GetCoachAsync(id)).GetProperty("version").GetUInt32();

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/coaches/{id}", _adminToken, new { firstName = "Yeni", lastName = "Ad", phone = (string?)null, version });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("User.AlreadyInactive");
        (await GetCoachAsync(id)).GetProperty("firstName").GetString().ShouldBe("Ayşe");
    }

    [Fact]
    public async Task Update_without_a_version_is_rejected()
    {
        var (id, _) = await CreatedCoachAsync();

        var response = await SendAsync(HttpMethod.Put, $"/api/v1/coaches/{id}", _adminToken, new { firstName = "Ayşe", lastName = "Yılmaz", phone = (string?)null });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("errors").GetProperty("version")[0].GetString().ShouldBe("Field.Required");
    }

    [Fact]
    public async Task Coaches_are_listed_by_last_name_with_status()
    {
        await CreatedCoachAsync("z@example.com", "Zorlu");
        var (inactive, _) = await CreatedCoachAsync("a@example.com", "Akın");
        await CreatedCoachAsync("m@example.com", "Mutlu");
        await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{inactive}/deactivate", _adminToken);
        var other = await SeedSchoolAsync("baska-okul");
        await SeedUserAsync("other@example.com", UserRole.Coach, other);
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, _school);

        var response = await SendAsync(HttpMethod.Get, "/api/v1/coaches?pageSize=2", _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        page.GetProperty("totalCount").GetInt32().ShouldBe(3);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("lastName").GetString()).ShouldBe(["Akın", "Mutlu"]);
        items[0].GetProperty("status").GetString().ShouldBe("Deactivated");
    }

    [Fact]
    public async Task Coaches_of_another_school_are_not_found()
    {
        var (id, _) = await CreatedCoachAsync();
        var other = await SeedSchoolAsync("baska-okul");
        var otherAdmin = TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", other.Value, Api.Clock.GetUtcNow());

        (await SendAsync(HttpMethod.Get, $"/api/v1/coaches/{id}", otherAdmin)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Put, $"/api/v1/coaches/{id}", otherAdmin, new { firstName = "X", lastName = "Y", phone = (string?)null, version = 1u }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var deactivate = await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{id}/deactivate", otherAdmin);
        deactivate.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(deactivate)).ShouldBe("User.NotFound");
        var list = await (await SendAsync(HttpMethod.Get, "/api/v1/coaches", otherAdmin)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        list.GetProperty("totalCount").GetInt32().ShouldBe(0);

        (await GetCoachAsync(id)).GetProperty("status").GetString().ShouldBe("Active");
    }

    [Fact]
    public async Task School_admins_are_not_coaches()
    {
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, _school);

        (await SendAsync(HttpMethod.Get, $"/api/v1/coaches/{admin.Id.Value}", _adminToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{admin.Id.Value}/deactivate", _adminToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Coach")]
    [InlineData("PlatformAdmin")]
    public async Task Only_school_admins_manage_coaches(string role)
    {
        var token = TestTokens.Create(Guid.CreateVersion7(), role, role == "PlatformAdmin" ? null : _school.Value, Api.Clock.GetUtcNow());

        (await CreateCoachAsync(token: token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/v1/coaches", token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
