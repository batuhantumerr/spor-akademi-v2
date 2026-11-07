using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// P2-F4 welcome e-mails (DEC-351): a new School Admin or Coach account gets an e-mail after commit with a single-use
/// set-password link, never the temporary password. The admin still sees the temporary password once (US-01, US-08).
/// </summary>
[Trait("Story", "P2-F4")]
public sealed class WelcomeEmailTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string NewPassword = "Benim-Sifrem-2027";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_school_admin_gets_a_welcome_link_without_the_temporary_password()
    {
        var platform = TestTokens.Create(Guid.CreateVersion7(), "PlatformAdmin", null, Api.Clock.GetUtcNow());
        var created = await SendAsync(HttpMethod.Post, "/api/v1/platform/schools", platform, new
        {
            name = "Moda Yüzme",
            slug = "moda-yuzme",
            timeZone = "Europe/Istanbul",
            admin = new { firstName = "Elif", lastName = "Demir", email = "elif@example.com" },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var temporaryPassword = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("temporaryPassword").GetString()!;
        Api.Emails.Sent.ShouldBeEmpty();

        await DeliverAsync();

        var mail = Api.Emails.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe("elif@example.com");
        mail.Template.ShouldBe("identity.welcome");
        mail.TextBody.ShouldContain("Moda Yüzme");
        mail.TextBody.ShouldContain("okul yöneticisi");
        mail.TextBody.ShouldNotContain(temporaryPassword);
        mail.HtmlBody.ShouldNotContain(temporaryPassword);

        (await ConfirmAsync(CapturingEmailSender.TokenIn(mail), NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var session = await LoginOkAsync("elif@example.com", NewPassword);
        session.Body.User.MustChangePassword.ShouldBeFalse();
        (await LoginAsync("elif@example.com", temporaryPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_new_coach_gets_a_welcome_link_valid_for_one_day()
    {
        var school = await SeedSchoolAsync();
        var admin = TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", school.Value, Api.Clock.GetUtcNow());
        (await CreateCoachAsync(admin, "ayse@example.com")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await DeliverAsync();
        var mail = Api.Emails.To("ayse@example.com").ShouldHaveSingleItem();
        mail.TextBody.ShouldContain("antrenör");
        mail.TextBody.ShouldContain("24 saat");

        Api.Clock.Advance(TimeSpan.FromHours(24));
        (await ErrorCodeAsync(await ConfirmAsync(CapturingEmailSender.TokenIn(mail), NewPassword))).ShouldBe("Auth.ResetTokenInvalid");
    }

    [Fact]
    public async Task No_link_is_sent_once_the_user_chose_a_password_and_changing_it_voids_a_sent_link()
    {
        var school = await SeedSchoolAsync();
        var admin = TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", school.Value, Api.Clock.GetUtcNow());
        var first = await CreateCoachAsync(admin, "ayse@example.com");
        var second = await CreateCoachAsync(admin, "mert@example.com");
        var ayseTemporary = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("temporaryPassword").GetString()!;
        var mertTemporary = (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("temporaryPassword").GetString()!;

        // Ayşe sets her password with the temporary one before the e-mail goes out: no link is sent.
        var ayse = await LoginOkAsync("ayse@example.com", ayseTemporary);
        (await SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", ayse.AccessToken, new { currentPassword = ayseTemporary, newPassword = NewPassword }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        Api.Emails.To("ayse@example.com").ShouldBeEmpty();

        // Mert got the link but changed his password with the temporary one: the link no longer works.
        var link = CapturingEmailSender.TokenIn(Api.Emails.To("mert@example.com").ShouldHaveSingleItem());
        var mert = await LoginOkAsync("mert@example.com", mertTemporary);
        (await SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", mert.AccessToken, new { currentPassword = mertTemporary, newPassword = NewPassword }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ErrorCodeAsync(await ConfirmAsync(link, "Baska-Bir-Sifre-2027"))).ShouldBe("Auth.ResetTokenInvalid");
    }

    private Task<HttpResponseMessage> CreateCoachAsync(string adminToken, string email) =>
        SendAsync(HttpMethod.Post, "/api/v1/coaches", adminToken, new { firstName = "Ayşe", lastName = "Yılmaz", email, phone = (string?)null });

    private Task<HttpResponseMessage> ConfirmAsync(string token, string newPassword) =>
        Http.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword }, Ct);

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }
}
