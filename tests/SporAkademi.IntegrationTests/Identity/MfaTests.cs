using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// P5-01 (DEC-600 – DEC-609): TOTP as a second factor. A Platform Admin must have it (login stops at a challenge until the
/// enrolment is done), a School Admin may; the second step takes a code or a recovery code, once; wrong codes count
/// towards the lockout; a Platform Admin can reset someone's MFA, which is audited and e-mailed.
/// </summary>
[Trait("Story", "P5-01")]
public sealed class MfaTests(DatabaseFixture database) : IdentityTest(database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CodeFor(string secret, int stepOffset = 0) =>
        Totp.Compute(Base32.Decode(secret), Totp.StepOf(Api.Clock.GetUtcNow()) + stepOffset);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private Task<HttpResponseMessage> PostAnonymousAsync(string path, object body) => Http.PostAsJsonAsync(path, body, Ct);

    /// <summary>A school admin who turned MFA on through the settings; returns the secret and the recovery codes.</summary>
    private async Task<(string Secret, string[] RecoveryCodes)> EnrolSchoolAdminAsync(string email)
    {
        var session = await LoginOkAsync(email);
        var started = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/enrollment", session.AccessToken);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secret = (await BodyAsync(started)).GetProperty("secret").GetString()!;
        var confirmed = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/enrollment/confirm", session.AccessToken, new { code = CodeFor(secret) });
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codes = (await BodyAsync(confirmed)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        return (secret, codes);
    }

    /// <summary>The first login of a Platform Admin: enrolment through the challenge token. Returns the session, the secret and the codes.</summary>
    private async Task<(Session Session, string Secret, string[] RecoveryCodes)> EnrolPlatformAdminAsync(string email)
    {
        var login = await LoginAsync(email, OwnPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = (await BodyAsync(login)).GetProperty("mfaToken").GetString()!;
        var started = await PostAnonymousAsync("/api/v1/auth/mfa/enrollment/start", new { mfaToken = token });
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secret = (await BodyAsync(started)).GetProperty("secret").GetString()!;
        var done = await PostAnonymousAsync("/api/v1/auth/mfa/enrollment/confirm", new { mfaToken = token, code = CodeFor(secret) });
        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(done);
        var cookie = RefreshCookieOf(done);
        cookie.ShouldNotBeNull();
        var session = new Session(body.GetProperty("accessToken").GetString()!, cookie.Value, JsonSerializer.Deserialize<SessionBody>(body.GetRawText(), Json)!);
        return (session, secret, [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!)]);
    }

    private async Task<string> ChallengeAsync(string email, string expectedPurpose)
    {
        var login = await LoginAsync(email, OwnPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await BodyAsync(login);
        body.GetProperty("purpose").GetString().ShouldBe(expectedPurpose);
        RefreshCookieOf(login).ShouldBeNull();
        body.TryGetProperty("accessToken", out _).ShouldBeFalse();
        return body.GetProperty("mfaToken").GetString()!;
    }

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    [Fact]
    public async Task A_platform_admin_without_mfa_gets_no_tokens_until_the_enrolment_is_done()
    {
        var platform = await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);

        var login = await LoginAsync("platform@example.com", OwnPassword);

        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var challenge = await BodyAsync(login);
        challenge.GetProperty("purpose").GetString().ShouldBe("Enroll");
        challenge.GetProperty("expiresIn").GetInt32().ShouldBe(5 * 60);
        RefreshCookieOf(login).ShouldBeNull();
        var token = challenge.GetProperty("mfaToken").GetString()!;

        var started = await PostAnonymousAsync("/api/v1/auth/mfa/enrollment/start", new { mfaToken = token });
        var start = await BodyAsync(started);
        var secret = start.GetProperty("secret").GetString()!;
        Base32.Decode(secret).Length.ShouldBe(20); // 160 bits
        start.GetProperty("otpAuthUri").GetString().ShouldStartWith("otpauth://totp/Spor%20Akademi:platform%40example.com?secret=" + secret);

        // The secret is stored encrypted, bound to the user.
        var stored = await LoadMfaAsync(platform.Id);
        stored.Ciphertext.ShouldStartWith("v2.k1."); // V1-03: the key id is part of the text (DEC-685)
        stored.Ciphertext.ShouldNotContain(secret);
        stored.Confirmed.ShouldBeFalse();

        var wrong = await PostAnonymousAsync("/api/v1/auth/mfa/enrollment/confirm", new { mfaToken = token, code = CodeFor(secret, 5) });
        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(wrong)).ShouldBe("Mfa.CodeInvalid");
        RefreshCookieOf(wrong).ShouldBeNull();

        var done = await PostAnonymousAsync("/api/v1/auth/mfa/enrollment/confirm", new { mfaToken = token, code = CodeFor(secret) });
        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(done);
        body.GetProperty("user").GetProperty("role").GetString().ShouldBe("PlatformAdmin");
        RefreshCookieOf(done).ShouldNotBeNull();
        var recovery = body.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        recovery.Length.ShouldBe(10);
        recovery.Distinct().Count().ShouldBe(10);
        recovery.ShouldAllBe(c => c.Length == 19 && c[4] == '-');

        // Recovery codes are stored hashed, never in clear.
        var rows = await LoadMfaAsync(platform.Id);
        rows.Confirmed.ShouldBeTrue();
        rows.CodeHashes.Count.ShouldBe(10);
        rows.CodeHashes.ShouldAllBe(h => h.Length == 64 && !recovery.Any(c => h.Contains(c.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase)));

        // The token is used up.
        var again = await PostAnonymousAsync("/api/v1/auth/mfa/enrollment/confirm", new { mfaToken = token, code = CodeFor(secret) });
        (await ErrorCodeAsync(again)).ShouldBe("Mfa.ChallengeInvalid");

        await DeliverAsync();
        Api.Emails.To("platform@example.com").Select(m => m.Template).ShouldBe(["identity.mfa-changed"]);
    }

    [Fact]
    public async Task Login_with_mfa_is_two_steps_and_the_code_works_once()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");

        // Step one: the password gives a challenge and nothing else.
        var token = await ChallengeAsync("admin@example.com", "Verify");

        // The code the enrolment used is spent (replay), even though it is still inside its time window.
        var replay = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) });
        replay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(replay)).ShouldBe("Mfa.CodeInvalid");

        // The next time step is accepted; a bad token is not.
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var bad = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = "x" + token, code = CodeFor(secret) });
        bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(bad)).ShouldBe("Mfa.ChallengeInvalid");
        var ok = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await SessionFromAsync(ok);
        (await SendAsync(HttpMethod.Get, "/api/v1/me", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The challenge is single use, and so is the code: a second login cannot reuse that step.
        (await ErrorCodeAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret, 1) })))
            .ShouldBe("Mfa.ChallengeInvalid");
        var second = await ChallengeAsync("admin@example.com", "Verify");
        (await ErrorCodeAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = second, code = CodeFor(secret) })))
            .ShouldBe("Mfa.CodeInvalid");
    }

    [Fact]
    public async Task A_challenge_expires_after_five_minutes()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        var token = await ChallengeAsync("admin@example.com", "Verify");

        Api.Clock.Advance(TimeSpan.FromMinutes(5));

        var late = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) });
        late.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(late)).ShouldBe("Mfa.ChallengeInvalid");
    }

    [Fact]
    public async Task A_recovery_code_logs_in_once_and_the_owner_is_told()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (_, codes) = await EnrolSchoolAdminAsync("admin@example.com");
        await DeliverAsync();
        Api.Emails.Clear();

        // Case, spaces and dashes do not matter when typing one.
        var typed = codes[2].Replace("-", " ").ToLowerInvariant();
        var first = await ChallengeAsync("admin@example.com", "Verify");
        (await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = first, code = typed })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await ChallengeAsync("admin@example.com", "Verify");
        var reused = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = second, code = codes[2] });
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(reused)).ShouldBe("Mfa.CodeInvalid");
        (await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = second, code = codes[3] })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await DeliverAsync();
        var mails = Api.Emails.To("admin@example.com");
        mails.Count.ShouldBe(2);
        mails.ShouldAllBe(m => m.Template == "identity.mfa-changed" && m.Subject == "Spor Akademi hesabınızda kurtarma kodu kullanıldı");
        foreach (var mail in mails)
        {
            mail.TextBody.ShouldNotContain(codes[2]);
            mail.TextBody.ShouldNotContain(codes[3]);
        }
    }

    [Fact]
    public async Task Wrong_codes_count_towards_the_account_lockout()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        var token = await ChallengeAsync("admin@example.com", "Verify");

        for (var i = 0; i < 4; i++)
        {
            var wrong = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = "000000" });
            (await ErrorCodeAsync(wrong)).ShouldBe("Mfa.CodeInvalid");
        }

        // The fifth failure locks the account (DEC-010) and so ends the challenge.
        var fifth = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = "000000" });
        fifth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(fifth)).ShouldBe("Auth.AccountLocked");
        (await ErrorCodeAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret, 1) })))
            .ShouldBe("Auth.AccountLocked");
        (await ErrorCodeAsync(await LoginAsync("admin@example.com", OwnPassword))).ShouldBe("Auth.AccountLocked");

        Api.Clock.Advance(TimeSpan.FromMinutes(16));
        var fresh = await ChallengeAsync("admin@example.com", "Verify");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        (await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = fresh, code = CodeFor(secret) })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_codes_and_wrong_passwords_share_one_failure_count()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await EnrolSchoolAdminAsync("admin@example.com");

        // Two wrong passwords, then three wrong codes: five failures in all. A right password in between clears nothing.
        for (var i = 0; i < 2; i++)
        {
            (await LoginAsync("admin@example.com", "Yanlis-Sifre-2027")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var token = await ChallengeAsync("admin@example.com", "Verify");
        for (var i = 0; i < 2; i++)
        {
            (await ErrorCodeAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = "000000" }))).ShouldBe("Mfa.CodeInvalid");
        }

        (await ErrorCodeAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = "000000" }))).ShouldBe("Auth.AccountLocked");
    }

    [Fact]
    public async Task A_school_admin_can_turn_mfa_off_with_a_current_code_and_a_platform_admin_cannot()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        var token = await ChallengeAsync("admin@example.com", "Verify");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var session = await SessionFromAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) }));

        var status = await BodyAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/mfa", session.AccessToken));
        status.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        status.GetProperty("required").GetBoolean().ShouldBeFalse();
        status.GetProperty("recoveryCodesRemaining").GetInt32().ShouldBe(10);

        var wrong = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/disable", session.AccessToken, new { code = "000000" });
        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(wrong)).ShouldBe("Mfa.CodeInvalid");

        // The code that logged in is spent; the next step's code turns it off.
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var off = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/disable", session.AccessToken, new { code = CodeFor(secret) });
        off.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LoginAsync("admin@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A Platform Admin cannot: it is mandatory.
        var (platformSession, platformSecret, _) = await EnrolPlatformAdminAsync("platform@example.com");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var refused = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/disable", platformSession.AccessToken, new { code = CodeFor(platformSecret) });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(refused)).ShouldBe("Mfa.RequiredForRole");
        (await BodyAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/mfa", platformSession.AccessToken))).GetProperty("required").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Coaches_cannot_use_mfa()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        (await SendAsync(HttpMethod.Get, "/api/v1/me/mfa", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/enrollment", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_platform_admin_resets_a_school_admins_mfa_which_is_audited_ends_the_sessions_and_is_mailed()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        var token = await ChallengeAsync("admin@example.com", "Verify");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var adminSession = await SessionFromAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) }));
        var (platform, _, _) = await EnrolPlatformAdminAsync("platform@example.com");
        await DeliverAsync();
        Api.Emails.Clear();

        var invalid = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{admin.Id.Value}/mfa/reset", platform.AccessToken, new { reason = "because I said so" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{admin.Id.Value}/mfa/reset", platform.AccessToken, new { reason = "LostDevice" });
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Gone: no credential, no codes, and the old session cannot be renewed.
        var after = await LoadMfaAsync(admin.Id);
        (after.Ciphertext, after.CodeHashes.Count).ShouldBe((null, 0));
        (await RefreshAsync(adminSession.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync("admin@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.OK); // a School Admin may do without

        // The audit log of the school names who, what and the fixed reason code; no values.
        var entries = await AcrossSchoolsAsync(db => db.Database
            .SqlQuery<string>($"SELECT concat_ws('|', user_role, action, tenant_id, reason, array_to_string(fields, ',')) AS \"Value\" FROM audit.audit_entries WHERE entity_type = 'User' AND entity_id = {admin.Id.Value} AND reason IS NOT NULL")
            .ToListAsync(Ct));
        entries.ShouldBe([$"PlatformAdmin|Update|{school.Value}|LostDevice|MfaEnabled"]);

        await DeliverAsync();
        var mail = Api.Emails.To("admin@example.com").ShouldHaveSingleItem();
        mail.Subject.ShouldBe("Spor Akademi iki adımlı doğrulamanız sıfırlandı");
        Api.Emails.To("platform@example.com").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_platform_admin_resets_another_platform_admin_but_not_themselves()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var other = await SeedUserAsync("other@example.com", UserRole.PlatformAdmin, null);
        var (platform, _, _) = await EnrolPlatformAdminAsync("platform@example.com");
        await EnrolPlatformAdminAsync("other@example.com");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        var own = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{platform.Body.User.Id}/mfa/reset", platform.AccessToken, new { reason = "Other" });
        own.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(own)).ShouldBe("Mfa.CannotResetOwn");

        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{other.Id.Value}/mfa/reset", platform.AccessToken, new { reason = "SuspectedCompromise" });
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The other one must enrol again at the next login: the role requires it.
        await ChallengeAsync("other@example.com", "Enroll");
        (await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{other.Id.Value}/mfa/reset", platform.AccessToken, new { reason = "Other" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Only_a_platform_admin_can_reset_and_a_school_admin_cannot_reach_another_schools_admin()
    {
        var schoolA = await SeedSchoolAsync("okul-a");
        var schoolB = await SeedSchoolAsync("okul-b");
        await SeedUserAsync("admin-a@example.com", UserRole.SchoolAdmin, schoolA);
        var adminB = await SeedUserAsync("admin-b@example.com", UserRole.SchoolAdmin, schoolB);
        await EnrolSchoolAdminAsync("admin-b@example.com");
        var asA = await LoginOkAsync("admin-a@example.com");

        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{adminB.Id.Value}/mfa/reset", asA.AccessToken, new { reason = "LostDevice" });

        reset.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoadMfaAsync(adminB.Id)).Confirmed.ShouldBeTrue();
        // A's status says nothing about B's, and A's own MFA is not B's.
        (await BodyAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/mfa", asA.AccessToken))).GetProperty("enabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_platform_admin_session_that_predates_the_rule_cannot_be_renewed()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var (session, _, _) = await EnrolPlatformAdminAsync("platform@example.com");
        await ExecuteSqlAsync("UPDATE identity.users SET mfa_enabled = false");

        var refreshed = await RefreshAsync(session.RefreshToken);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(refreshed)).ShouldBe("Auth.SessionExpired");
    }

    [Fact]
    public async Task A_newer_login_replaces_the_earlier_challenge()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        var token = await ChallengeAsync("admin@example.com", "Verify");
        var other = await LoginAsync("admin@example.com", OwnPassword); // a second login replaces the first challenge
        other.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        (await ErrorCodeAsync(await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) })))
            .ShouldBe("Mfa.ChallengeInvalid");
        (await LoadUserAsync(admin.Id)).MfaEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Production_refuses_to_start_with_the_platform_admin_rule_switched_off()
    {
        // DEC-608: the switch exists for local development; a production host must not start without the mandatory MFA.
        using var api = Api.WithWebHostBuilder(builder =>
        {
            ApiFactory.UseProductionRateLimits(builder);
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Mfa:RequirePlatformAdmin"] = "false" }));
        });

        Should.Throw<Exception>(() => api.CreateClient()).ToString().ShouldContain("Mfa:RequirePlatformAdmin");
    }

    [Fact]
    public void Production_refuses_to_start_without_an_encryption_key_for_the_secrets()
    {
        // DEC-602: outside Development and Testing the JWT key does not stand in; a missing or malformed key stops the start.
        foreach (var key in new[] { string.Empty, "not-a-key" })
        {
            using var api = Api.WithWebHostBuilder(builder =>
            {
                ApiFactory.UseProductionRateLimits(builder);
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Mfa:EncryptionKey"] = key }));
            });

            Should.Throw<Exception>(() => api.CreateClient()).ToString().ShouldContain("Mfa:EncryptionKey");
        }
    }

    [Fact]
    public async Task A_secret_that_cannot_be_decrypted_is_a_stable_error_not_a_500_and_not_a_failed_attempt()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        var token = await ChallengeAsync("admin@example.com", "Verify");
        // As after a changed key: the stored ciphertext is no longer ours.
        await ExecuteSqlAsync("UPDATE identity.mfa_credentials SET secret_ciphertext = 'v1.' || repeat('A', 64)");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        var response = await PostAnonymousAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("Mfa.SecretUnreadable");
        (await LoadUserAsync(admin.Id)).FailedLoginCount.ShouldBe(0);
    }

    [Fact]
    public async Task Turning_mfa_on_ends_the_sessions_that_existed_before()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var stolen = await LoginOkAsync("admin@example.com");
        var own = await LoginOkAsync("admin@example.com");
        var started = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/enrollment", own.AccessToken);
        var secret = (await BodyAsync(started)).GetProperty("secret").GetString()!;

        (await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/enrollment/confirm", own.AccessToken, new { code = CodeFor(secret) })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RefreshAsync(stolen.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(own.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_reset_clears_an_enrolment_that_was_started_but_never_confirmed()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var session = await LoginOkAsync("admin@example.com");
        (await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/enrollment", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var (platform, _, _) = await EnrolPlatformAdminAsync("platform@example.com");

        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{admin.Id.Value}/mfa/reset", platform.AccessToken, new { reason = "Other" });

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LoadMfaAsync(admin.Id)).Ciphertext.ShouldBeNull();
    }

    [Fact]
    public async Task Where_the_rule_is_switched_off_in_development_a_platform_admin_signs_in_with_the_password_alone()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        using var api = Api.WithWebHostBuilder(builder => builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Mfa:RequirePlatformAdmin"] = "false" })));
        using var http = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "platform@example.com", password = OwnPassword }, Ct);

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed record MfaRows(string? Ciphertext, bool Confirmed, IReadOnlyList<string> CodeHashes);

    private Task<MfaRows> LoadMfaAsync(UserId id) => AcrossSchoolsAsync(async db =>
    {
        var credential = await db.Set<MfaCredential>().AsNoTracking().SingleOrDefaultAsync(c => c.UserId == id, Ct);
        var hashes = await db.Set<MfaRecoveryCode>().AsNoTracking().Where(c => c.UserId == id).Select(c => c.CodeHash).ToListAsync(Ct);
        return new MfaRows(credential?.SecretCiphertext, credential?.IsConfirmed ?? false, hashes);
    });
}
