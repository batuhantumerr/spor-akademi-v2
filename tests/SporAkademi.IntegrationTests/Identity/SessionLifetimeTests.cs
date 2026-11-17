using System.Globalization;
using System.Net;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// P5-02 (DEC-610 – DEC-614): the panel roles (Platform Admin, School Admin, Coach) have a 30 minute idle timeout (the
/// longest gap between two refreshes) and a 12 hour absolute lifetime; the Parent role keeps 7 days sliding and 30
/// days absolute. The clock is the fake one of the test host.
/// </summary>
[Trait("Story", "P5-02")]
public sealed class SessionLifetimeTests(DatabaseFixture database) : IdentityTest(database)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private async Task<Session> SessionOfAsync(UserRole role)
    {
        SchoolId? school = role == UserRole.PlatformAdmin ? null : await SeedSchoolAsync();
        if (role == UserRole.Parent)
        {
            await SeedParentAsync("user@example.com", school!.Value);
        }
        else
        {
            await SeedUserAsync("user@example.com", role, school);
        }

        return await LoginOkAsync("user@example.com");
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(UserRole.SchoolAdmin)]
    [InlineData(UserRole.Coach)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task A_panel_session_that_stays_idle_for_thirty_minutes_ends(UserRole role)
    {
        var session = await SessionOfAsync(role);
        Api.Clock.Advance(Idle);

        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.SessionExpired");
    }

    [Fact]
    public async Task A_panel_session_survives_gaps_just_under_thirty_minutes_and_the_cookie_lives_that_long()
    {
        var session = await SessionOfAsync(UserRole.SchoolAdmin);
        var token = session.RefreshToken;

        for (var i = 0; i < 4; i++)
        {
            Api.Clock.Advance(Idle - TimeSpan.FromSeconds(1));
            var response = await RefreshAsync(token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            RefreshCookieOf(response)!.Attributes["max-age"].ShouldBe(Seconds(Idle));
            token = RefreshCookieOf(response)!.Value;
        }
    }

    [Theory]
    [InlineData(UserRole.SchoolAdmin)]
    [InlineData(UserRole.Coach)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task A_panel_session_ends_twelve_hours_after_login_however_active_it_is(UserRole role)
    {
        var session = await SessionOfAsync(role);
        // The login was 11 hours ago (the rate limit of the auth endpoints rules out refreshing around the clock).
        await ExecuteSqlAsync("UPDATE identity.refresh_tokens SET family_started_at = family_started_at - interval '11 hours', family_expires_at = family_expires_at - interval '11 hours'");
        Api.Clock.Advance(TimeSpan.FromMinutes(20));

        var first = await RefreshAsync(session.RefreshToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        RefreshCookieOf(first)!.Attributes["max-age"].ShouldBe(Seconds(Idle)); // 40 minutes of the session are left
        Api.Clock.Advance(TimeSpan.FromMinutes(29));
        var second = await RefreshAsync(RefreshCookieOf(first)!.Value);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Eleven minutes of the session remain, so the cookie lives eleven minutes, not thirty.
        RefreshCookieOf(second)!.Attributes["max-age"].ShouldBe(Seconds(TimeSpan.FromMinutes(11)));

        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        var last = await RefreshAsync(RefreshCookieOf(second)!.Value);
        last.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(last)).ShouldBe("Auth.SessionExpired");
    }

    [Fact]
    public async Task The_access_token_never_outlives_the_session()
    {
        var session = await SessionOfAsync(UserRole.SchoolAdmin);
        // Ten minutes of the twelve hours remain after this refresh, less than the access token's fifteen.
        await ExecuteSqlAsync("UPDATE identity.refresh_tokens SET family_started_at = family_started_at - interval '11 hours 40 minutes', family_expires_at = family_expires_at - interval '11 hours 40 minutes'");
        Api.Clock.Advance(TimeSpan.FromMinutes(10));

        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var renewed = await SessionFromAsync(response);
        Decode(renewed.AccessToken).ValidTo.ShouldBe(Api.Clock.GetUtcNow().AddMinutes(10).UtcDateTime);
        RefreshCookieOf(response)!.Attributes["max-age"].ShouldBe(Seconds(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task An_expired_panel_session_is_not_a_reuse_and_other_devices_stay_signed_in()
    {
        var session = await SessionOfAsync(UserRole.SchoolAdmin);
        Api.Clock.Advance(TimeSpan.FromMinutes(20));
        var otherDevice = await LoginOkAsync("user@example.com");
        Api.Clock.Advance(TimeSpan.FromMinutes(15));

        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The first device went idle; the second is 15 minutes old and unaffected, and nothing was revoked.
        var user = await LoadUserAsync(UserId.From(session.Body.User.Id));
        user.RefreshTokens.Count(t => t.RevokedAt is not null).ShouldBe(0);
        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_parent_session_is_unchanged_seven_days_sliding_and_thirty_days_absolute()
    {
        var session = await SessionOfAsync(UserRole.Parent);
        Api.Clock.Advance(TimeSpan.FromHours(13));

        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RefreshCookieOf(response)!.Attributes["max-age"].ShouldBe(Seconds(TimeSpan.FromDays(7)));
        var token = RefreshCookieOf(response)!.Value;

        for (var day = 6; day <= 24; day += 6)
        {
            Api.Clock.Advance(TimeSpan.FromDays(6));
            var next = await RefreshAsync(token);
            next.StatusCode.ShouldBe(HttpStatusCode.OK);
            token = RefreshCookieOf(next)!.Value;
        }

        Api.Clock.Advance(TimeSpan.FromDays(6));
        (await RefreshAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_created_before_the_change_follows_the_new_idle_rule_at_its_next_refresh()
    {
        var session = await SessionOfAsync(UserRole.SchoolAdmin);
        // The token as the old code issued it: seven days to live, a thirty day session counted from the login.
        await ExecuteSqlAsync("UPDATE identity.refresh_tokens SET expires_at = issued_at + interval '7 days', family_expires_at = family_started_at + interval '30 days'");
        Api.Clock.Advance(TimeSpan.FromHours(2));

        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.SessionExpired");
    }

    [Fact]
    public async Task A_session_created_before_the_change_follows_the_twelve_hour_limit_at_its_next_refresh()
    {
        var session = await SessionOfAsync(UserRole.SchoolAdmin);
        // Recently refreshed, but the login was 13 hours ago and the session still had weeks to go.
        await ExecuteSqlAsync("UPDATE identity.refresh_tokens SET expires_at = issued_at + interval '7 days', family_started_at = family_started_at - interval '13 hours', family_expires_at = family_started_at - interval '13 hours' + interval '30 days'");
        Api.Clock.Advance(TimeSpan.FromMinutes(10));

        var response = await RefreshAsync(session.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_lost_response_retry_inside_the_grace_window_still_works_for_a_panel_session()
    {
        var session = await SessionOfAsync(UserRole.SchoolAdmin);
        Api.Clock.Advance(TimeSpan.FromMinutes(20));
        var first = await RefreshAsync(session.RefreshToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        Api.Clock.Advance(TimeSpan.FromSeconds(5));

        var retry = await RefreshAsync(session.RefreshToken);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        RefreshCookieOf(retry)!.Attributes["max-age"].ShouldBe(Seconds(Idle));
    }
}
