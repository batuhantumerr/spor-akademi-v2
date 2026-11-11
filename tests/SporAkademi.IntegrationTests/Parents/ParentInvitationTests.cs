using System.Net;
using System.Net.Http.Json;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Parents;

/// <summary>
/// P3-02 (DEC-383): the School Admin invites a guardian with an e-mail address; the 72-hour, single-use link sets the
/// parent's first password. Resending voids the earlier link; revoking closes the account; the status is visible.
/// </summary>
[Trait("Story", "P3-02")]
public sealed class ParentInvitationTests(DatabaseFixture database) : ParentTest(database)
{
    private const string Email = "ayse.veli@example.com";

    [Fact]
    public async Task An_invited_guardian_accepts_the_link_and_signs_in_as_a_parent()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        (await StatusOfAsync(guardian)).ShouldBe("NotInvited");

        var invited = await InviteAsync(guardian);
        invited.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(invited)).GetProperty("status").GetString().ShouldBe("Invited");
        Api.Emails.Sent.ShouldBeEmpty(); // after commit, through the outbox

        await DeliverAsync();
        var mail = Api.Emails.To(Email).ShouldHaveSingleItem();
        mail.Template.ShouldBe("identity.parent-invitation");
        mail.TextBody.ShouldContain("72 saat");
        mail.TextBody.ShouldContain("/reset-password#token=");
        (await InvitationAsync(guardian)).GetProperty("invitationExpiresAt").GetDateTimeOffset().ShouldBe(Api.Clock.GetUtcNow().AddHours(72));

        (await AcceptAsync(InvitationTokenFor(Email))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var session = await LoginOkAsync(Email, ParentPassword);

        session.Body.User.Role.ShouldBe("Parent");
        session.Body.User.TenantId.ShouldBe(School.Value);
        session.Body.User.MustChangePassword.ShouldBeFalse();
        (await StatusOfAsync(guardian)).ShouldBe("Active");
        var shown = (await GetAsync(student)).GetProperty("guardians")[0].GetProperty("invitation");
        shown.GetProperty("status").GetString().ShouldBe("Active");
        shown.GetProperty("invitationExpiresAt").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task The_link_expires_after_seventy_two_hours()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();

        using var admin = AdvanceTo(Api.Clock.GetUtcNow().AddHours(72));

        (await ErrorCodeAsync(await AcceptAsync(InvitationTokenFor(Email)))).ShouldBe("Auth.ResetTokenInvalid");
        var status = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/guardians/{guardian}/invitation", Ct);
        status.GetProperty("status").GetString().ShouldBe("Invited");
    }

    [Fact]
    public async Task Resending_voids_the_earlier_link()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        var first = InvitationTokenFor(Email);
        PastInvitationCooldown();

        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // At once, before the new e-mail is even sent.
        (await ErrorCodeAsync(await AcceptAsync(first))).ShouldBe("Auth.ResetTokenInvalid");
        await DeliverAsync();
        Api.Emails.To(Email).Count.ShouldBe(2);
        (await AcceptAsync(InvitationTokenFor(Email))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Revoking_a_pending_invitation_voids_the_link_and_inviting_again_reopens_it()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        var link = InvitationTokenFor(Email);

        var revoked = await RevokeAsync(guardian);

        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(revoked)).GetProperty("status").GetString().ShouldBe("Revoked");
        (await ErrorCodeAsync(await AcceptAsync(link))).ShouldBe("Auth.ResetTokenInvalid");
        PastInvitationCooldown();

        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOfAsync(guardian)).ShouldBe("Invited");
        await DeliverAsync();
        (await AcceptAsync(InvitationTokenFor(Email))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusOfAsync(guardian)).ShouldBe("Active");
    }

    [Fact]
    public async Task Revoking_an_active_account_ends_its_sessions_and_its_access_at_once()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        var (parent, session) = await SignedInParentAsync(guardian, Email);
        IdsIn(await GetOkAsync(parent, "/api/v1/parent/children")).ShouldBe([student]);

        (await RevokeAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StatusOfAsync(guardian)).ShouldBe("Revoked");
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(Email, ParentPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // The access token is still valid for minutes, but the account no longer sees any child.
        (await GetOkAsync(parent, "/api/v1/parent/children")).GetArrayLength().ShouldBe(0);
        (await parent.GetAsync(new Uri($"/api/v1/parent/children/{student}/dues", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        parent.Dispose();
    }

    [Fact]
    public async Task A_re_invited_account_does_not_bring_back_an_access_token_from_before_the_revocation()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        var (parent, _) = await SignedInParentAsync(guardian, Email);
        (await RevokeAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        PastInvitationCooldown();

        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The account is active again but waits for a new password: the old token still sees nothing.
        (await GetOkAsync(parent, "/api/v1/parent/children")).GetArrayLength().ShouldBe(0);
        (await parent.GetAsync(new Uri($"/api/v1/parent/children/{student}/schedule", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        parent.Dispose();
    }

    [Fact]
    public async Task Another_invitation_within_two_minutes_is_refused()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var again = await InviteAsync(guardian);

        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("ParentAccount.InvitationTooSoon");
        await DeliverAsync();
        Api.Emails.To(Email).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Changing_the_address_voids_a_pending_link()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", "yanlis.adres@example.com");
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        var mistyped = InvitationTokenFor("yanlis.adres@example.com");

        await ChangeGuardianEmailAsync(student, Email);

        (await ErrorCodeAsync(await AcceptAsync(mistyped))).ShouldBe("Auth.ResetTokenInvalid");
        (await StatusOfAsync(guardian)).ShouldBe("Revoked");
        PastInvitationCooldown();
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        (await AcceptAsync(InvitationTokenFor(Email))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LoginAsync(Email, ParentPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_or_clearing_the_address_of_an_active_account_ends_its_access()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        var (parent, session) = await SignedInParentAsync(guardian, Email);

        await ChangeGuardianEmailAsync(student, email: null);

        (await StatusOfAsync(guardian)).ShouldBe("Revoked");
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetOkAsync(parent, "/api/v1/parent/children")).GetArrayLength().ShouldBe(0);
        parent.Dispose();
    }

    [Fact]
    public async Task Saving_the_guardian_with_the_same_address_keeps_the_account()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        using var _ = (await SignedInParentAsync(guardian, Email)).Client;

        await ChangeGuardianEmailAsync(student, Email.ToUpperInvariant());

        (await StatusOfAsync(guardian)).ShouldBe("Active");
    }

    [Fact]
    public async Task The_parent_api_is_rate_limited_per_account()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        using var parent = (await SignedInParentAsync(guardian, Email)).Client;

        for (var i = 0; i < 60; i++)
        {
            (await parent.GetAsync(new Uri("/api/v1/parent/children", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await parent.GetAsync(new Uri("/api/v1/parent/children", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task An_active_parent_is_not_invited_again()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        using var _ = (await SignedInParentAsync(guardian, Email)).Client;

        var again = await InviteAsync(guardian);

        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("ParentAccount.AlreadyActive");
    }

    [Fact]
    public async Task A_guardian_without_an_e_mail_address_cannot_be_invited()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", guardianEmail: null);

        var response = await InviteAsync(guardian);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Guardian.EmailRequired");
        (await StatusOfAsync(guardian)).ShouldBe("NotInvited");
    }

    [Fact]
    public async Task An_address_another_account_uses_is_a_clear_conflict()
    {
        await SeedUserAsync(Email, UserRole.Coach, School);
        var (_, guardian) = await RegisterChildAsync("Ali", Email);

        var response = await InviteAsync(guardian);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("User.EmailTaken");
        (await StatusOfAsync(guardian)).ShouldBe("NotInvited");
        await DeliverAsync();
        Api.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_guardians_with_the_same_address_cannot_both_have_an_account()
    {
        var (_, mother) = await RegisterChildAsync("Ali", Email);
        var (_, other) = await RegisterChildAsync("Veli", Email, lastName: "Başka");
        (await InviteAsync(mother)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CodeOf(await InviteAsync(other))).ShouldBe("User.EmailTaken");
    }

    [Fact]
    public async Task A_guardian_whose_students_all_left_is_not_invited()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        (await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = Today }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await InviteAsync(guardian);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Guardian.NoActiveStudent");
    }

    [Fact]
    public async Task Revoking_a_guardian_that_was_never_invited_says_so()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);

        (await CodeOf(await RevokeAsync(guardian))).ShouldBe("ParentAccount.NotInvited");
    }

    [Fact]
    public async Task Accepting_invitations_is_rate_limited_like_password_reset()
    {
        for (var i = 0; i < 10; i++)
        {
            (await AcceptAsync($"bilinmeyen-davet-{i}")).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        (await AcceptAsync("bilinmeyen-davet-son")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Coaches_cannot_invite()
    {
        var (_, guardian) = await RegisterChildAsync("Ali", Email);
        using var coach = CoachClient(Ayse);

        (await InviteAsync(guardian, coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RevokeAsync(guardian, coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<System.Text.Json.JsonElement> GetAsync(Guid student) =>
        await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/students/{student}", Ct);
}
