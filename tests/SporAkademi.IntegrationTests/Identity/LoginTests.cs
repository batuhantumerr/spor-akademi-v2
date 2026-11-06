using System.Net;
using System.Security.Cryptography;
using System.Text;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

[Trait("Story", "US-03")]
public sealed class LoginTests(DatabaseFixture database) : IdentityTest(database)
{
    [Fact]
    public async Task Valid_credentials_return_an_access_token_and_a_refresh_cookie()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        var response = await LoginAsync("  Coach@Example.com ", OwnPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await SessionFromAsync(response);
        session.Body.ExpiresIn.ShouldBe(15 * 60);
        session.Body.User.ShouldBe(new UserBody(coach.Id.Value, "coach@example.com", "Deniz", "Kaya", "Coach", school.Value, false));

        var token = Decode(session.AccessToken);
        token.Issuer.ShouldBe("spor-akademi");
        token.Audiences.ShouldBe(["spor-akademi-api"]);
        token.ValidTo.ShouldBe(Api.Clock.GetUtcNow().AddMinutes(15).UtcDateTime);
        token.Header.Alg.ShouldBe("HS256");
        var claims = token.Claims.ToDictionary(c => c.Type, c => c.Value, StringComparer.Ordinal);
        claims["sub"].ShouldBe(coach.Id.Value.ToString());
        claims["role"].ShouldBe("Coach");
        claims["tenant_id"].ShouldBe(school.Value.ToString());
        claims["pwd_change_required"].ShouldBe("false");
        claims["jti"].ShouldNotBeNullOrWhiteSpace();
        // No personal data in the token (DEC-052).
        claims.Keys.Order(StringComparer.Ordinal).ShouldBe(["aud", "exp", "iat", "iss", "jti", "nbf", "pwd_change_required", "role", "sub", "tenant_id"]);

        var cookie = RefreshCookieOf(response)!;
        cookie.Value.Length.ShouldBe(43); // 256 bits, base64url
        cookie.Attributes["path"].ShouldBe("/api/v1/auth");
        cookie.Attributes["max-age"].ShouldBe("604800");
        cookie.Attributes["samesite"].ShouldBe("strict");
        cookie.Attributes.ShouldContainKey("secure");
        cookie.Attributes.ShouldContainKey("httponly");

        // Only the SHA-256 of the token is stored.
        var stored = await LoadUserAsync(coach.Id);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookie.Value)));
        stored.RefreshTokens.ShouldContain(t => t.TokenHash == expectedHash);
        stored.RefreshTokens.ShouldNotContain(t => t.TokenHash.Contains(cookie.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_access_token_authenticates_until_it_expires_after_15_minutes()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        (await SendAsync(HttpMethod.Get, "/api/v1/me", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Api.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(31));
        (await SendAsync(HttpMethod.Get, "/api/v1/me", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Platform_admin_token_has_no_tenant()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);

        var session = await LoginOkAsync("platform@example.com");

        Decode(session.AccessToken).Claims.ShouldNotContain(c => c.Type == "tenant_id");
        Decode(session.AccessToken).Claims.Single(c => c.Type == "role").Value.ShouldBe("PlatformAdmin");
        session.Body.User.TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_get_the_same_answer()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        var wrongPassword = await LoginAsync("coach@example.com", "Yanlis-Sifre-123");
        var unknownEmail = await LoginAsync("nobody@example.com", "Yanlis-Sifre-123");
        var malformedEmail = await LoginAsync("not-an-email", "Yanlis-Sifre-123");

        foreach (var response in new[] { wrongPassword, unknownEmail, malformedEmail })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ErrorCodeAsync(response)).ShouldBe("Auth.InvalidCredentials");
            RefreshCookieOf(response).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Missing_fields_are_a_validation_error()
    {
        var response = await LoginAsync("", "");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe("Validation.Failed");
    }

    [Fact]
    public async Task Five_failures_within_15_minutes_lock_the_account_for_15_minutes()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        for (var i = 0; i < 4; i++)
        {
            (await ErrorCodeAsync(await LoginAsync("coach@example.com", "Yanlis-Sifre-123"))).ShouldBe("Auth.InvalidCredentials");
            Api.Clock.Advance(TimeSpan.FromMinutes(3));
        }

        (await ErrorCodeAsync(await LoginAsync("coach@example.com", "Yanlis-Sifre-123"))).ShouldBe("Auth.AccountLocked");

        // Locked: even the right password is refused until the 15 minutes are over.
        Api.Clock.Advance(TimeSpan.FromMinutes(14));
        var locked = await LoginAsync("coach@example.com", OwnPassword);
        locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(locked)).ShouldBe("Auth.AccountLocked");

        Api.Clock.Advance(TimeSpan.FromMinutes(1));
        (await LoginAsync("coach@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Failures_spread_over_more_than_15_minutes_do_not_lock()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        for (var i = 0; i < 4; i++)
        {
            await LoginAsync("coach@example.com", "Yanlis-Sifre-123");
        }

        Api.Clock.Advance(TimeSpan.FromMinutes(15));
        (await ErrorCodeAsync(await LoginAsync("coach@example.com", "Yanlis-Sifre-123"))).ShouldBe("Auth.InvalidCredentials");

        (await LoginAsync("coach@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deactivated_account_cannot_log_in()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        await DeactivateUserAsync(coach.Id);

        var response = await LoginAsync("coach@example.com", OwnPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.AccountInactive");
        // Without the password, the status is not revealed.
        (await ErrorCodeAsync(await LoginAsync("coach@example.com", "Yanlis-Sifre-123"))).ShouldBe("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task Users_of_a_deactivated_school_cannot_log_in()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await ExecuteSqlAsync("UPDATE tenancy.schools SET status = 'Deactivated'");

        var response = await LoginAsync("admin@example.com", OwnPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("Auth.AccountInactive");
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_client()
    {
        for (var i = 0; i < 10; i++)
        {
            (await LoginAsync("nobody@example.com", "Yanlis-Sifre-123")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await LoginAsync("nobody@example.com", "Yanlis-Sifre-123")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
