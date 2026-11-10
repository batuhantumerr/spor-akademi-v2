using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-05")]
public sealed class PasswordTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string NewPassword = "Yeni-Sifrem-2027";

    private Task<HttpResponseMessage> ChangePasswordAsync(string accessToken, string current, string next) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", accessToken, new { currentPassword = current, newPassword = next });

    private static string ResetCoachPath(User coach) => $"/api/v1/coaches/{coach.Id.Value}/reset-password";

    [Fact]
    public async Task With_a_temporary_password_only_the_password_change_is_allowed()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school, temporary: true);
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        var session = await LoginOkAsync("admin@example.com", TemporaryPassword);

        session.Body.User.MustChangePassword.ShouldBeTrue();
        Decode(session.AccessToken).Claims.Single(c => c.Type == "pwd_change_required").Value.ShouldBe("true");
        var blocked = await SendAsync(HttpMethod.Post, ResetCoachPath(coach), session.AccessToken);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(blocked)).ShouldBe("Auth.PasswordChangeRequired");
        (await SendAsync(HttpMethod.Get, "/api/v1/me", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LogoutAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var fresh = await LoginOkAsync("admin@example.com", TemporaryPassword);
        var changed = await ChangePasswordAsync(fresh.AccessToken, TemporaryPassword, NewPassword);

        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterChange = await SessionFromAsync(changed);
        afterChange.Body.User.MustChangePassword.ShouldBeFalse();
        Decode(afterChange.AccessToken).Claims.Single(c => c.Type == "pwd_change_required").Value.ShouldBe("false");
        (await SendAsync(HttpMethod.Post, ResetCoachPath(coach), afterChange.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoadUserAsync(admin.Id)).MustChangePassword.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Kisa-sifr")]
    [InlineData("Kisa-sifre1")]
    [InlineData("123456789")]
    [InlineData("1234567890")]
    [InlineData("qwertyuiop")]
    [InlineData("sifre12345")]
    [InlineData("qwerty123456")]
    public async Task New_password_must_follow_the_policy(string weak)
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school, temporary: true);
        var session = await LoginOkAsync("coach@example.com", TemporaryPassword);

        var response = await ChangePasswordAsync(session.AccessToken, TemporaryPassword, weak);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Validation.Failed");
        problem.GetProperty("errors").GetProperty("newPassword")[0].GetString().ShouldBe("Auth.PasswordTooWeak");
    }

    [Fact]
    public async Task Wrong_current_password_is_rejected()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        var response = await ChangePasswordAsync(session.AccessToken, "Yanlis-Sifre-123", NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.CurrentPasswordIncorrect");
        (await LoginAsync("coach@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_the_password_ends_all_other_sessions()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var otherDevice = await LoginOkAsync("coach@example.com");
        var thisDevice = await LoginOkAsync("coach@example.com");

        var changed = await SessionFromAsync(await ChangePasswordAsync(thisDevice.AccessToken, OwnPassword, NewPassword));

        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(thisDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(changed.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ErrorCodeAsync(await LoginAsync("coach@example.com", OwnPassword))).ShouldBe("Auth.InvalidCredentials");
        (await LoginAsync("coach@example.com", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "a", newPassword = NewPassword }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task School_admin_resets_a_coach_password_to_a_temporary_one()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var coachSession = await LoginOkAsync("coach@example.com");
        var admin = await LoginOkAsync("admin@example.com");

        var response = await SendAsync(HttpMethod.Post, ResetCoachPath(coach), admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var temporary = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("temporaryPassword").GetString()!;
        temporary.Length.ShouldBe(16);
        (await RefreshAsync(coachSession.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(await LoginAsync("coach@example.com", OwnPassword))).ShouldBe("Auth.InvalidCredentials");
        var withTemporary = await LoginOkAsync("coach@example.com", temporary);
        withTemporary.Body.User.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task Coaches_of_another_school_are_not_found()
    {
        var school = await SeedSchoolAsync();
        var otherSchool = await SeedSchoolAsync("besiktas-spor");
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var otherCoach = await SeedUserAsync("coach@example.com", UserRole.Coach, otherSchool);
        var admin = await LoginOkAsync("admin@example.com");

        var response = await SendAsync(HttpMethod.Post, ResetCoachPath(otherCoach), admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("User.NotFound");
        (await LoginAsync("coach@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Coaches_cannot_reset_passwords()
    {
        var school = await SeedSchoolAsync();
        var colleague = await SeedUserAsync("colleague@example.com", UserRole.Coach, school);
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var coach = await LoginOkAsync("coach@example.com");

        (await SendAsync(HttpMethod.Post, ResetCoachPath(colleague), coach.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Platform_admin_resets_a_school_admin_password()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var schoolAdmin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var platform = await LoginOkAsync("platform@example.com");

        var response = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/platform/schools/{school.Value}/admins/{schoolAdmin.Id.Value}/reset-password",
            platform.AccessToken);
        var wrongSchool = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/platform/schools/{Guid.CreateVersion7()}/admins/{schoolAdmin.Id.Value}/reset-password",
            platform.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        wrongSchool.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LoadUserAsync(schoolAdmin.Id)).MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task Me_returns_the_profile_of_the_caller()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        var me = await (await SendAsync(HttpMethod.Get, "/api/v1/me", session.AccessToken))
            .Content.ReadFromJsonAsync<UserBody>(TestContext.Current.CancellationToken);

        me.ShouldBe(new UserBody(coach.Id.Value, "coach@example.com", "Deniz", "Kaya", "Coach", school.Value, false));
    }

    [Fact]
    public async Task Me_returns_the_school_time_zone_for_school_users_only()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);

        var coach = await LoginOkAsync("coach@example.com");
        var platform = await LoginOkAsync("platform@example.com");
        var coachMe = await (await SendAsync(HttpMethod.Get, "/api/v1/me", coach.AccessToken))
            .Content.ReadFromJsonAsync<MeTimeZoneBody>(TestContext.Current.CancellationToken);
        var platformMe = await (await SendAsync(HttpMethod.Get, "/api/v1/me", platform.AccessToken))
            .Content.ReadFromJsonAsync<MeTimeZoneBody>(TestContext.Current.CancellationToken);

        coachMe!.SchoolTimeZone.ShouldBe("Europe/Istanbul");
        platformMe!.SchoolTimeZone.ShouldBeNull();
    }

    private sealed record MeTimeZoneBody(string? SchoolTimeZone);
}
