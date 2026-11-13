using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// P4-03 (ASVS 2.2.3, DEC-512): a change of the password is e-mailed to the account's own address, so a takeover does
/// not go unnoticed. The e-mail names no password, token or link. The first password a user sets themselves (the
/// temporary one, a welcome or an invitation link) is not news and sends nothing.
/// </summary>
[Trait("Story", "P4-03")]
public sealed class PasswordChangedNotificationTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string NewPassword = "Yepyeni-Sifre-2027";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changing_the_password_tells_the_owner_without_naming_anything_secret()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        var changed = await SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", session.AccessToken, new { currentPassword = OwnPassword, newPassword = NewPassword });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        Api.Emails.Sent.ShouldBeEmpty(); // after commit, through the outbox
        await DeliverAsync();

        var mail = Api.Emails.To("coach@example.com").ShouldHaveSingleItem();
        mail.Template.ShouldBe("identity.password-changed");
        mail.Subject.ShouldBe("Spor Akademi şifreniz değiştirildi");
        foreach (var body in new[] { mail.TextBody, mail.HtmlBody })
        {
            body.ShouldNotContain(OwnPassword);
            body.ShouldNotContain(NewPassword);
            body.ShouldNotContain("token");
            body.ShouldNotContain("http");
        }
    }

    [Fact]
    public async Task A_reset_through_the_link_tells_the_owner_once_the_password_is_set()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        (await Http.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "coach@example.com" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await DeliverAsync();
        var token = CapturingEmailSender.TokenIn(Api.Emails.To("coach@example.com").ShouldHaveSingleItem());

        (await Http.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword = NewPassword }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await DeliverAsync();

        Api.Emails.To("coach@example.com").Select(m => m.Template).ShouldBe(["identity.password-reset", "identity.password-changed"]);
    }

    [Fact]
    public async Task A_new_temporary_password_from_the_school_admin_tells_the_owner()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var admin = await LoginOkAsync("admin@example.com");

        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{coach.Id.Value}/reset-password", admin.AccessToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        var temporary = (await reset.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("temporaryPassword").GetString()!;
        await DeliverAsync();

        var mail = Api.Emails.To("coach@example.com").ShouldHaveSingleItem();
        mail.Template.ShouldBe("identity.password-changed");
        mail.TextBody.ShouldNotContain(temporary);
        mail.HtmlBody.ShouldNotContain(temporary);
    }

    [Fact]
    public async Task The_first_password_a_user_sets_is_not_announced()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school, temporary: true);
        var session = await LoginOkAsync("coach@example.com", TemporaryPassword);

        var changed = await SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", session.AccessToken, new { currentPassword = TemporaryPassword, newPassword = OwnPassword });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();

        Api.Emails.Sent.ShouldBeEmpty();
    }

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }
}
