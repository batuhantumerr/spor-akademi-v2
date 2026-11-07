using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// P2-F4 self-service password reset (DEC-352, DEC-353): the request never reveals whether an account exists, the link
/// is e-mailed after commit, is single-use, short-lived, stored as a hash and tied to the user, and using it ends
/// every session.
/// </summary>
[Trait("Story", "P2-F4")]
public sealed class PasswordResetTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string NewPassword = "Yepyeni-Sifre-2027";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Known_unknown_and_deactivated_addresses_get_the_same_answer_and_only_the_known_one_a_link()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var gone = await SeedUserAsync("gone@example.com", UserRole.Coach, school);
        await DeactivateUserAsync(gone.Id);

        var known = await RequestAsync("Coach@Example.com");
        var unknown = await RequestAsync("nobody@example.com");
        var deactivated = await RequestAsync("gone@example.com");
        var malformed = await RequestAsync("not-an-address");

        foreach (var response in new[] { known, unknown, deactivated, malformed })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        }

        // Nothing is sent inside the request: the e-mail goes out after commit, through the outbox.
        Api.Emails.Sent.ShouldBeEmpty();
        await DeliverAsync();
        var mail = Api.Emails.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe("coach@example.com");
        mail.Template.ShouldBe("identity.password-reset");
        mail.Subject.ShouldBe("Spor Akademi şifre sıfırlama");
        mail.TextBody.ShouldContain("http://localhost:4200/reset-password#token=");
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_and_it_belongs_to_the_user()
    {
        var school = await SeedSchoolAsync();
        var user = await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        var token = await RequestAndReadTokenAsync("coach@example.com");

        var stored = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<PasswordToken>().AsNoTracking().SingleAsync(Ct));
        stored.UserId.ShouldBe(user.Id);
        stored.Purpose.ShouldBe(PasswordTokenPurpose.Reset);
        stored.TokenHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        stored.TokenHash.ShouldNotContain(token);
        stored.ExpiresAt.ShouldBe(Api.Clock.GetUtcNow().AddMinutes(30));
        // The outbox message holds the user's id only, never the token or the address.
        var payloads = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<OutboxMessage>().AsNoTracking().Select(m => m.Payload).ToListAsync(Ct));
        payloads.ShouldAllBe(p => !p.Contains(token) && !p.Contains("coach@example.com"));
    }

    [Fact]
    public async Task The_link_sets_a_new_password_once_and_ends_every_session()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");
        var token = await RequestAndReadTokenAsync("coach@example.com");

        (await ConfirmAsync(token, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync("coach@example.com", OwnPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginOkAsync("coach@example.com", NewPassword)).Body.User.MustChangePassword.ShouldBeFalse();

        var again = await ConfirmAsync(token, "Baska-Bir-Sifre-2027");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(again)).ShouldBe("Auth.ResetTokenInvalid");
    }

    [Fact]
    public async Task An_expired_or_unknown_link_is_refused_without_saying_why()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var token = await RequestAndReadTokenAsync("coach@example.com");

        Api.Clock.Advance(TimeSpan.FromMinutes(30));

        var expired = await ConfirmAsync(token, NewPassword);
        var unknown = await ConfirmAsync("bilinmeyen-bir-token-degeri-0123456789abcdef", NewPassword);
        (await ErrorCodeAsync(expired)).ShouldBe("Auth.ResetTokenInvalid");
        (await ErrorCodeAsync(unknown)).ShouldBe("Auth.ResetTokenInvalid");
        (await LoginOkAsync("coach@example.com")).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_weak_password_is_refused_and_the_link_stays_usable()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var token = await RequestAndReadTokenAsync("coach@example.com");

        var weak = await ConfirmAsync(token, "kisa");
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await weak.Content.ReadAsStringAsync(Ct)).ShouldContain("Auth.PasswordTooWeak");

        (await ConfirmAsync(token, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task One_account_gets_one_link_per_cooldown_and_only_the_newest_link_works()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var first = await RequestAndReadTokenAsync("coach@example.com");

        (await RequestAsync("coach@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DeliverAsync();
        Api.Emails.Sent.Count.ShouldBe(1);

        Api.Clock.Advance(User.PasswordResetRequestCooldown);
        Api.Emails.Clear();
        var second = await RequestAndReadTokenAsync("coach@example.com");

        (await ErrorCodeAsync(await ConfirmAsync(first, NewPassword))).ShouldBe("Auth.ResetTokenInvalid");
        (await ConfirmAsync(second, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_locked_account_can_be_reset_and_signs_in_again()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        for (var i = 0; i < User.MaxFailedLogins; i++)
        {
            await LoginAsync("coach@example.com", "Yanlis-Sifre-123");
        }

        (await ErrorCodeAsync(await LoginAsync("coach@example.com", OwnPassword))).ShouldBe("Auth.AccountLocked");
        var token = await RequestAndReadTokenAsync("coach@example.com");

        (await ConfirmAsync(token, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LoginOkAsync("coach@example.com", NewPassword)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_signed_in_caller_gets_202_but_no_link()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var other = await SeedSchoolAsync("baska-okul");
        var token = TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", other.Value, Api.Clock.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password-reset/request")
        {
            Content = JsonContent.Create(new { email = "coach@example.com" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await Http.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await DeliverAsync();
        Api.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Requests_of_an_inactive_school_send_nothing()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        await ExecuteSqlAsync("UPDATE tenancy.schools SET status = 'Deactivated'");

        (await RequestAsync("coach@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await DeliverAsync();
        Api.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Requests_are_rate_limited_per_client()
    {
        for (var i = 0; i < 5; i++)
        {
            (await RequestAsync($"kisi{i}@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        (await RequestAsync("kisi5@example.com")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        // Login keeps its own budget.
        (await LoginAsync("nobody@example.com", "Yanlis-Sifre-123")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_smtp_failure_is_retried_by_the_outbox_and_the_retry_sends_a_fresh_link()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        (await RequestAsync("coach@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Api.Emails.FailWith = new IOException("SMTP down");

        await DeliverAsync();

        var message = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(Ct));
        message.ProcessedAt.ShouldBeNull();
        message.LastError.ShouldBe(typeof(IOException).FullName);

        Api.Emails.FailWith = null;
        Api.Clock.Advance(TimeSpan.FromMinutes(1));
        await DeliverAsync();
        var token = CapturingEmailSender.TokenIn(Api.Emails.Sent.ShouldHaveSingleItem());
        (await ConfirmAsync(token, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private Task<HttpResponseMessage> RequestAsync(string email) =>
        Http.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email }, Ct);

    private Task<HttpResponseMessage> ConfirmAsync(string token, string newPassword) =>
        Http.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword }, Ct);

    private async Task<string> RequestAndReadTokenAsync(string email)
    {
        (await RequestAsync(email)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DeliverAsync();
        return CapturingEmailSender.TokenIn(Api.Emails.To(email)[^1]);
    }

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }
}
