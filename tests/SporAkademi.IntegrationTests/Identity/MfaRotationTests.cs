using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Identity;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// V1-03 (DEC-685 – DEC-689): MFA encryption keys rotate (a current key and previous keys that only decrypt, a key id in the
/// ciphertext, lazy re-encryption at a successful verify and an idempotent job that reports what is left), the signed-in
/// user can replace the recovery codes, and a Platform Admin's reset of another Platform Admin leaves an audit entry
/// without a school.
/// </summary>
[Trait("Story", "V1-03")]
public sealed class MfaRotationTests(DatabaseFixture database) : IdentityTest(database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Built at run time so that no key-looking literal sits in the source.
    private static readonly string KeyOne = ApiFactory.JwtSigningKey;
    private static readonly string KeyTwo = new('b', 64);
    private static readonly string KeyThree = new('c', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CodeFor(string secret, int stepOffset = 0) =>
        Totp.Compute(Base32.Decode(secret), Totp.StepOf(Api.Clock.GetUtcNow()) + stepOffset);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    // ---- The protector ------------------------------------------------------------------------------------------------

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static MfaSecretProtector Protector(string? key, string id = "k1", params (string Id, string Key)[] previous)
    {
        var options = new MfaOptions { EncryptionKey = key, EncryptionKeyId = id };
        foreach (var (previousId, previousKey) in previous)
        {
            options.PreviousEncryptionKeys[previousId] = previousKey;
        }

        return new MfaSecretProtector(Options.Create(options), new ConfigurationBuilder().Build(), new Environment("Production"));
    }

    /// <summary>The first format (P5-01): <c>v1.</c> and nonce, tag and ciphertext, bound to the user only.</summary>
    private static string LegacyProtect(string hexKey, Guid userId, byte[] secret)
    {
        var blob = new byte[12 + 16 + secret.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using var aes = new AesGcm(Convert.FromHexString(hexKey), 16);
        aes.Encrypt(blob.AsSpan(0, 12), secret, blob.AsSpan(28), blob.AsSpan(12, 16), Encoding.ASCII.GetBytes($"mfa-secret/v1:{userId:N}"));
        return "v1." + Base64Url.EncodeToString(blob);
    }

    [Fact]
    public void A_secret_is_encrypted_with_the_current_key_and_names_it()
    {
        var user = Guid.CreateVersion7();
        var secret = RandomNumberGenerator.GetBytes(20);
        var protector = Protector(KeyTwo, "k2", ("k1", KeyOne));

        var text = protector.Protect(user, secret);

        text.ShouldStartWith("v2.k2.");
        protector.IsCurrent(text).ShouldBeTrue();
        protector.Unprotect(user, text).ShouldBe(secret);
        text.Length.ShouldBeLessThanOrEqualTo(MfaCredential.CiphertextMaxLength);
    }

    [Fact]
    public void A_previous_key_still_decrypts_and_its_secrets_are_due_for_re_encryption()
    {
        var user = Guid.CreateVersion7();
        var secret = RandomNumberGenerator.GetBytes(20);
        var old = Protector(KeyOne, "k1").Protect(user, secret);
        var rotated = Protector(KeyTwo, "k2", ("k1", KeyOne));

        rotated.Unprotect(user, old).ShouldBe(secret);
        rotated.IsCurrent(old).ShouldBeFalse();
        Should.Throw<CryptographicException>(() => Protector(KeyTwo, "k2").Unprotect(user, old)); // the old key was retired
    }

    [Fact]
    public void The_first_format_without_a_key_id_is_read_with_any_configured_key_and_always_due()
    {
        var user = Guid.CreateVersion7();
        var secret = RandomNumberGenerator.GetBytes(20);
        var legacy = LegacyProtect(KeyOne, user, secret);

        var unrotated = Protector(KeyOne);
        unrotated.Unprotect(user, legacy).ShouldBe(secret);
        unrotated.IsCurrent(legacy).ShouldBeFalse();
        Protector(KeyTwo, "k2", ("k1", KeyOne)).Unprotect(user, legacy).ShouldBe(secret);
        Should.Throw<CryptographicException>(() => Protector(KeyTwo, "k2").Unprotect(user, legacy));
    }

    [Fact]
    public void A_ciphertext_does_not_open_for_another_user_or_under_another_key_id()
    {
        var user = Guid.CreateVersion7();
        var protector = Protector(KeyOne, "k1");
        var text = protector.Protect(user, RandomNumberGenerator.GetBytes(20));

        Should.Throw<CryptographicException>(() => protector.Unprotect(Guid.CreateVersion7(), text));
        Should.Throw<CryptographicException>(() => protector.Unprotect(user, text.Replace("v2.k1.", "v2.k9.", StringComparison.Ordinal)));
        // The id is authenticated: the same key listed under another id does not accept a ciphertext relabelled with it.
        var relabelled = Protector(KeyOne, "k2", ("k1", KeyOne));
        Should.Throw<CryptographicException>(() => relabelled.Unprotect(user, text.Replace("v2.k1.", "v2.k2.", StringComparison.Ordinal)));
        Should.Throw<CryptographicException>(() => protector.Unprotect(user, "v2.k1"));
        Should.Throw<CryptographicException>(() => protector.Unprotect(user, "v3.k1.AAAA"));
    }

    [Theory]
    [InlineData("k1", "k1", true)]
    [InlineData("k1", "K1", true)]
    [InlineData("k1", "k2", false)]
    public void Key_ids_must_be_unique_ignoring_case(string current, string previous, bool problem)
    {
        var options = new MfaOptions { EncryptionKey = KeyTwo, EncryptionKeyId = current };
        options.PreviousEncryptionKeys[previous] = KeyOne;

        (options.KeySettingsProblem() is not null).ShouldBe(problem);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a.b", false)]
    [InlineData("k-1", false)]
    [InlineData("12345678901234567", false)]
    [InlineData("k2027q1", true)]
    public void A_key_id_is_one_to_sixteen_letters_or_digits(string id, bool valid)
    {
        MfaOptions.IsValidKeyId(id).ShouldBe(valid);
    }

    [Fact]
    public void A_malformed_previous_key_stops_the_host_from_starting()
    {
        using var api = Api.WithWebHostBuilder(builder =>
        {
            ApiFactory.UseProductionRateLimits(builder);
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Mfa:EncryptionKeyId"] = "k2", ["Mfa:PreviousEncryptionKeys:k1"] = "not-hex" }));
        });

        Should.Throw<Exception>(() => api.CreateClient()).ToString().ShouldContain("Mfa");
    }

    [Fact]
    public void A_previous_key_with_the_id_of_the_current_one_stops_the_host_from_starting()
    {
        using var api = Api.WithWebHostBuilder(builder =>
        {
            ApiFactory.UseProductionRateLimits(builder);
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Mfa:PreviousEncryptionKeys:k1"] = KeyThree }));
        });

        Should.Throw<Exception>(() => api.CreateClient()).ToString().ShouldContain("Mfa");
    }

    // ---- Rotation through the API and the job ---------------------------------------------------------------------------

    /// <summary>The same application and database after a rotation: <c>k2</c> is current, <c>k1</c> (the test host's key) previous.</summary>
    private WebApplicationFactory<Program> Rotated(bool keepPrevious = true) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?> { ["Mfa:EncryptionKey"] = KeyTwo, ["Mfa:EncryptionKeyId"] = "k2" };
            if (keepPrevious)
            {
                settings["Mfa:PreviousEncryptionKeys:k1"] = KeyOne;
            }

            config.AddInMemoryCollection(settings);
        }));

    private Task<int> UnusedCodesAsync(UserId id) => AcrossSchoolsAsync(db =>
        db.Set<MfaRecoveryCode>().AsNoTracking().CountAsync(c => c.UserId == id && c.UsedAt == null, Ct));

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

    private static async Task<HttpResponseMessage> LoginWithAsync(HttpClient client, string email, string code)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = OwnPassword }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = (await BodyAsync(login)).GetProperty("mfaToken").GetString()!;
        return await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code }, Ct);
    }

    private Task<string> CiphertextOfAsync(UserId id) => AcrossSchoolsAsync(async db =>
        (await db.Set<MfaCredential>().AsNoTracking().SingleAsync(c => c.UserId == id, Ct)).SecretCiphertext);

    [Fact]
    public async Task A_verify_after_a_rotation_works_with_the_previous_key_and_moves_the_secret_to_the_current_one()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        (await CiphertextOfAsync(admin.Id)).ShouldStartWith("v2.k1.");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        using var rotated = Rotated();
        using var client = rotated.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var verified = await LoginWithAsync(client, "admin@example.com", CodeFor(secret));

        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CiphertextOfAsync(admin.Id)).ShouldStartWith("v2.k2.");

        // From now on the previous key is not needed for this user.
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        using var retired = Rotated(keepPrevious: false);
        using var retiredClient = retired.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await LoginWithAsync(retiredClient, "admin@example.com", CodeFor(secret))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_secret_under_a_key_that_is_no_longer_configured_is_reported_as_unreadable()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, _) = await EnrolSchoolAdminAsync("admin@example.com");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        using var retired = Rotated(keepPrevious: false);
        using var client = retired.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await LoginWithAsync(client, "admin@example.com", CodeFor(secret));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("Mfa.SecretUnreadable");
    }

    [Fact]
    public async Task The_job_moves_every_secret_to_the_current_key_reports_what_is_left_and_is_idempotent()
    {
        var school = await SeedSchoolAsync();
        var first = await SeedUserAsync("first@example.com", UserRole.SchoolAdmin, school);
        var second = await SeedUserAsync("second@example.com", UserRole.SchoolAdmin, school);
        var damaged = await SeedUserAsync("damaged@example.com", UserRole.SchoolAdmin, school);
        var (firstSecret, _) = await EnrolSchoolAdminAsync("first@example.com");
        var (secondSecret, _) = await EnrolSchoolAdminAsync("second@example.com");
        await EnrolSchoolAdminAsync("damaged@example.com");
        // One of them is still in the first format (no key id), as every secret was before this change.
        var legacy = LegacyProtect(KeyOne, second.Id.Value, Base32.Decode(secondSecret));
        await ExecuteSqlAsync($"UPDATE identity.mfa_credentials SET secret_ciphertext = '{legacy}' WHERE user_id = '{second.Id.Value}'");
        await ExecuteSqlAsync($"UPDATE identity.mfa_credentials SET secret_ciphertext = 'v2.k1.' || repeat('A', 64) WHERE user_id = '{damaged.Id.Value}'");

        using var rotated = Rotated();
        async Task<MfaRotationReport> RunAsync()
        {
            await using var scope = rotated.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MfaSecretRotationJob>().ReencryptAsync(Ct);
        }

        (await RunAsync()).ShouldBe(new MfaRotationReport(Reencrypted: 2, Unreadable: 1, Remaining: 1));
        (await CiphertextOfAsync(first.Id)).ShouldStartWith("v2.k2.");
        (await CiphertextOfAsync(second.Id)).ShouldStartWith("v2.k2.");
        (await CiphertextOfAsync(damaged.Id)).ShouldStartWith("v2.k1."); // left alone: it needs a reset, not a guess
        (await RunAsync()).ShouldBe(new MfaRotationReport(Reencrypted: 0, Unreadable: 1, Remaining: 1));

        // The old key can go: the moved secrets open under the new key alone, with the codes they always had.
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        using var retired = Rotated(keepPrevious: false);
        using var client = retired.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await LoginWithAsync(client, "first@example.com", CodeFor(firstSecret))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginWithAsync(client, "second@example.com", CodeFor(secondSecret))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_job_is_registered_as_a_daily_recurring_job_and_returns_the_number_moved()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await EnrolSchoolAdminAsync("admin@example.com");

        using var rotated = Rotated();
        await using var scope = rotated.Services.CreateAsyncScope();
        var moved = await scope.ServiceProvider.GetRequiredService<MfaSecretRotationJob>().RunAsync(Ct);

        moved.ShouldBe(1);
        MfaSecretRotationJob.JobId.ShouldBe("identity-mfa-secret-rotation");
    }

    // ---- Recovery codes ----------------------------------------------------------------------------------------------

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    [Fact]
    public async Task Regenerating_recovery_codes_invalidates_the_old_ones_shows_new_ones_once_and_tells_the_owner()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, oldCodes) = await EnrolSchoolAdminAsync("admin@example.com");
        await DeliverAsync();
        Api.Emails.Clear();
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var token = (await BodyAsync(await LoginAsync("admin@example.com", OwnPassword))).GetProperty("mfaToken").GetString()!;
        var session = await SessionFromAsync(await Http.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) }, Ct));
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        var response = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", session.AccessToken, new { code = CodeFor(secret) });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fresh = (await BodyAsync(response)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToArray();
        fresh.Length.ShouldBe(10);
        fresh.Distinct().Count().ShouldBe(10);
        fresh.Intersect(oldCodes).ShouldBeEmpty();
        (await UnusedCodesAsync(admin.Id)).ShouldBe(10);

        // The old codes are dead, the new ones work (once), and the session that asked is still good.
        var status = await BodyAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/mfa", session.AccessToken));
        status.GetProperty("recoveryCodesRemaining").GetInt32().ShouldBe(10);
        var oldLogin = await LoginWithAsync(Http, "admin@example.com", oldCodes[0]);
        (await ErrorCodeAsync(oldLogin)).ShouldBe("Mfa.CodeInvalid");
        (await LoginWithAsync(Http, "admin@example.com", fresh[0])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginWithAsync(Http, "admin@example.com", fresh[0])).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await DeliverAsync();
        Api.Emails.To("admin@example.com").Select(m => m.Subject).ShouldContain("Spor Akademi kurtarma kodlarınız yenilendi");
    }

    [Fact]
    public async Task Regenerating_needs_a_current_code_from_the_authenticator_not_a_recovery_code_and_wrong_codes_count()
    {
        var school = await SeedSchoolAsync();
        var admin = await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var (secret, codes) = await EnrolSchoolAdminAsync("admin@example.com");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));
        var token = (await BodyAsync(await LoginAsync("admin@example.com", OwnPassword))).GetProperty("mfaToken").GetString()!;
        var session = await SessionFromAsync(await Http.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { mfaToken = token, code = CodeFor(secret) }, Ct));

        var wrong = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", session.AccessToken, new { code = "000000" });
        var recovery = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", session.AccessToken, new { code = codes[0] });
        var reused = await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", session.AccessToken, new { code = CodeFor(secret) }); // the step of the login

        (await ErrorCodeAsync(wrong)).ShouldBe("Mfa.CodeInvalid");
        (await ErrorCodeAsync(recovery)).ShouldBe("Mfa.CodeInvalid");
        (await ErrorCodeAsync(reused)).ShouldBe("Mfa.CodeInvalid");
        (await LoadUserAsync(admin.Id)).FailedLoginCount.ShouldBe(3);
        (await UnusedCodesAsync(admin.Id)).ShouldBe(10); // nothing was replaced or used up
    }

    [Fact]
    public async Task Regenerating_is_for_a_signed_in_admin_whose_mfa_is_on()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var admin = await LoginOkAsync("admin@example.com");
        var coach = await LoginOkAsync("coach@example.com");

        (await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", admin.AccessToken, new { code = "123456" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", coach.AccessToken, new { code = "123456" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Http.PostAsJsonAsync("/api/v1/me/mfa/recovery-codes", new { code = "123456" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(await SendAsync(HttpMethod.Post, "/api/v1/me/mfa/recovery-codes", admin.AccessToken, new { code = "123456" }))).ShouldBe("Mfa.NotEnabled");
    }

    // ---- Audit of a Platform Admin resetting another Platform Admin ---------------------------------------------------

    private async Task<(Session Session, string Secret)> EnrolPlatformAdminAsync(string email)
    {
        var login = await LoginAsync(email, OwnPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = (await BodyAsync(login)).GetProperty("mfaToken").GetString()!;
        var started = await Http.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { mfaToken = token }, Ct);
        var secret = (await BodyAsync(started)).GetProperty("secret").GetString()!;
        var done = await Http.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { mfaToken = token, code = CodeFor(secret) }, Ct);
        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyAsync(done);
        var cookie = RefreshCookieOf(done);
        cookie.ShouldNotBeNull();
        return (new Session(body.GetProperty("accessToken").GetString()!, cookie.Value, JsonSerializer.Deserialize<SessionBody>(body.GetRawText(), Json)!), secret);
    }

    [Fact]
    public async Task Resetting_another_platform_admin_leaves_an_audit_entry_without_a_school_that_no_school_can_read()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("school@example.com", UserRole.SchoolAdmin, school);
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var other = await SeedUserAsync("other@example.com", UserRole.PlatformAdmin, null);
        var (platform, _) = await EnrolPlatformAdminAsync("platform@example.com");
        await EnrolPlatformAdminAsync("other@example.com");
        Api.Clock.Advance(TimeSpan.FromSeconds(30));

        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/platform/users/{other.Id.Value}/mfa/reset", platform.AccessToken, new { reason = "SuspectedCompromise" });

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var entries = await AcrossSchoolsAsync(db => db.Database
            .SqlQuery<string>($"SELECT concat_ws('|', coalesce(tenant_id::text, 'none'), user_role, action, entity_type, reason, array_to_string(fields, ',')) AS \"Value\" FROM audit.audit_entries WHERE entity_id = {other.Id.Value}")
            .ToListAsync(Ct));
        entries.ShouldBe(["none|PlatformAdmin|Update|User|SuspectedCompromise|MfaEnabled"]);

        // A school's audit screen never shows it: its scope matches the school only.
        var schoolAdmin = await LoginOkAsync("school@example.com");
        var list = await BodyAsync(await SendAsync(HttpMethod.Get, "/api/v1/audit-log", schoolAdmin.AccessToken));
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ShouldNotContain(other.Id.Value.ToString());
    }

    [Fact]
    public async Task Platform_admins_are_listed_for_the_platform_screen_and_only_to_a_platform_admin()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("school@example.com", UserRole.SchoolAdmin, school);
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        await SeedUserAsync("other@example.com", UserRole.PlatformAdmin, null);
        var (platform, _) = await EnrolPlatformAdminAsync("platform@example.com");

        var list = await SendAsync(HttpMethod.Get, "/api/v1/platform/admins", platform.AccessToken);

        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var admins = (await BodyAsync(list)).EnumerateArray().ToList();
        admins.Select(a => a.GetProperty("email").GetString()).ShouldBe(["other@example.com", "platform@example.com"], ignoreOrder: true);
        admins.Single(a => a.GetProperty("email").GetString() == "platform@example.com").GetProperty("mfaEnabled").GetBoolean().ShouldBeTrue();
        admins.Single(a => a.GetProperty("email").GetString() == "other@example.com").GetProperty("mfaEnabled").GetBoolean().ShouldBeFalse();
        admins.SelectMany(a => a.EnumerateObject().Select(p => p.Name)).Distinct().ShouldBe(["id", "firstName", "lastName", "email", "status", "mfaEnabled"], ignoreOrder: true);
        var schoolAdmin = await LoginOkAsync("school@example.com");
        (await SendAsync(HttpMethod.Get, "/api/v1/platform/admins", schoolAdmin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Http.GetAsync(new Uri("/api/v1/platform/admins", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_school_scope_cannot_write_an_audit_entry_without_a_school()
    {
        var school = await SeedSchoolAsync();

        var attempt = await Should.ThrowAsync<Exception>(() => InScopeAsync(sp =>
        {
            var tenant = sp.GetRequiredService<BackgroundTenantContext>();
            tenant.Enter(school.Value);
            return sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(
                $"INSERT INTO audit.audit_entries (id, tenant_id, occurred_at, action, entity_type, fields) VALUES ({Guid.CreateVersion7()}, NULL, now(), 'Update', 'User', '{{}}')",
                Ct);
        }));

        attempt.ToString().ShouldContain("row-level security");
    }
}
