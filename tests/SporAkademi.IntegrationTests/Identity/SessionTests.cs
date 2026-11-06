using System.Net;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-04")]
public sealed class SessionTests(DatabaseFixture database) : IdentityTest(database)
{
    private async Task<(User Coach, Session Session)> CoachSessionAsync()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        return (coach, await LoginOkAsync("coach@example.com"));
    }

    [Fact]
    public async Task Refresh_returns_a_new_token_pair_and_invalidates_the_old_refresh_token()
    {
        var (coach, first) = await CoachSessionAsync();
        Api.Clock.Advance(TimeSpan.FromMinutes(20));

        var response = await RefreshAsync(first.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await SessionFromAsync(response);
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        second.AccessToken.ShouldNotBe(first.AccessToken);
        second.Body.User.Id.ShouldBe(coach.Id.Value);
        Decode(second.AccessToken).ValidTo.ShouldBe(Api.Clock.GetUtcNow().AddMinutes(15).UtcDateTime);
        RefreshCookieOf(response)!.Attributes["max-age"].ShouldBe("604800");
        (await SendAsync(HttpMethod.Get, "/api/v1/me", second.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_all_sessions_of_the_user()
    {
        var (coach, first) = await CoachSessionAsync();
        var otherDevice = await LoginOkAsync("coach@example.com");
        var second = await SessionFromAsync(await RefreshAsync(first.RefreshToken));

        var reuse = await RefreshAsync(first.RefreshToken);

        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(reuse)).ShouldBe("Auth.SessionExpired");
        RefreshCookieOf(reuse)!.Attributes["expires"].ShouldContain("1970");
        (await RefreshAsync(second.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoadUserAsync(coach.Id)).RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public async Task Refresh_and_logout_require_the_x_requested_with_header()
    {
        var (_, session) = await CoachSessionAsync();

        var refresh = await RefreshAsync(session.RefreshToken, withHeader: false);
        var logout = await LogoutAsync(session.RefreshToken, withHeader: false);

        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        logout.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(refresh)).ShouldBe("Validation.Failed");
        // Nothing happened: the token still works.
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_without_or_with_an_unknown_cookie_is_an_expired_session()
    {
        var missing = await RefreshAsync(null);
        var unknown = await RefreshAsync("bm90LWEtcmVhbC10b2tlbg");

        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(missing)).ShouldBe("Auth.SessionExpired");
        (await ErrorCodeAsync(unknown)).ShouldBe("Auth.SessionExpired");
    }

    [Fact]
    public async Task Refresh_tokens_expire_after_seven_days()
    {
        var (_, session) = await CoachSessionAsync();

        Api.Clock.Advance(TimeSpan.FromDays(7));
        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.SessionExpired");
    }

    [Fact]
    public async Task A_session_ends_thirty_days_after_login_however_often_it_is_refreshed()
    {
        var (_, session) = await CoachSessionAsync();
        var token = session.RefreshToken;

        for (var day = 6; day <= 24; day += 6)
        {
            Api.Clock.Advance(TimeSpan.FromDays(6));
            var response = await RefreshAsync(token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            token = RefreshCookieOf(response)!.Value;
            if (day == 24)
            {
                // Only six days of the session remain, so the cookie lives six days.
                RefreshCookieOf(response)!.Attributes["max-age"].ShouldBe(TimeSpan.FromDays(6).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        Api.Clock.Advance(TimeSpan.FromDays(6));
        (await RefreshAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_current_refresh_token_and_clears_the_cookie()
    {
        var (_, session) = await CoachSessionAsync();
        var otherDevice = await LoginOkAsync("coach@example.com");

        var response = await LogoutAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cleared = RefreshCookieOf(response)!;
        cleared.Value.ShouldBeEmpty();
        cleared.Attributes["path"].ShouldBe("/api/v1/auth");
        cleared.Attributes["expires"].ShouldContain("1970");
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // Only this session ended.
        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_without_a_session_still_succeeds()
    {
        (await LogoutAsync(null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LogoutAsync("bm90LWEtcmVhbC10b2tlbg")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Deactivated_users_cannot_refresh()
    {
        var (coach, session) = await CoachSessionAsync();
        await DeactivateUserAsync(coach.Id);

        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Users_of_a_deactivated_school_cannot_refresh()
    {
        var (_, session) = await CoachSessionAsync();
        await ExecuteSqlAsync("UPDATE tenancy.schools SET status = 'Deactivated'");

        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.AccountInactive");
    }
}
