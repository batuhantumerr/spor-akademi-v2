using SporAkademi.Domain.Identity;

namespace SporAkademi.Domain.UnitTests.Identity;

/// <summary>P2-F4 (DEC-351 – DEC-353): set-password links are single-use, short-lived, one per user and tied to it.</summary>
[Trait("Story", "P2-F4")]
public sealed class PasswordTokenTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_account_raises_user_created_for_the_welcome_e_mail()
    {
        var user = UserTests.NewCoach();

        user.DomainEvents.OfType<UserCreated>().ShouldHaveSingleItem().ShouldBe(new UserCreated(user.Id, user.TenantId));
    }

    [Fact]
    public void Reset_links_live_thirty_minutes_and_welcome_links_one_day()
    {
        var user = UserTests.NewCoach();

        user.IssuePasswordToken("reset", PasswordTokenPurpose.Reset, Now).Value.ExpiresAt.ShouldBe(Now.AddMinutes(30));
        user.IssuePasswordToken("welcome", PasswordTokenPurpose.Welcome, Now).Value.ExpiresAt.ShouldBe(Now.AddHours(24));
    }

    [Fact]
    public void Setting_the_password_with_a_link_uses_it_up_ends_every_session_and_lifts_the_lock()
    {
        var user = UserTests.NewCoach();
        var first = user.CompleteLogin("r1", Now).Value;
        var second = user.CompleteLogin("r2", Now).Value;
        for (var i = 0; i < User.MaxFailedLogins; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.IssuePasswordToken("link", PasswordTokenPurpose.Reset, Now);

        user.SetPasswordWithToken("link", "new-hash", Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();

        user.PasswordHash.ShouldBe("new-hash");
        user.MustChangePassword.ShouldBeFalse();
        user.LockoutEndsAt.ShouldBeNull();
        user.FailedLoginCount.ShouldBe(0);
        first.RevokedAt.ShouldBe(Now.AddMinutes(5));
        second.RevokedAt.ShouldBe(Now.AddMinutes(5));
        // Revoked without a successor: the job panel session ends too (DEC-332).
        user.RefreshTokens.ShouldAllBe(t => t.ReplacedByTokenId == null);
        user.PasswordTokens.ShouldBeEmpty();
        user.DomainEvents.OfType<PasswordChanged>().ShouldHaveSingleItem();
        user.SetPasswordWithToken("link", "again", Now.AddMinutes(6)).Error.ShouldBe(AuthErrors.ResetTokenInvalid);
    }

    [Fact]
    public void An_expired_link_is_invalid_and_changes_nothing()
    {
        var user = UserTests.NewCoach();
        user.IssuePasswordToken("link", PasswordTokenPurpose.Reset, Now);

        user.SetPasswordWithToken("link", "new-hash", Now.AddMinutes(30)).Error.ShouldBe(AuthErrors.ResetTokenInvalid);

        user.PasswordHash.ShouldBe("hash");
        user.PasswordTokens.ShouldHaveSingleItem();
    }

    [Fact]
    public void Only_the_newest_link_works()
    {
        var user = UserTests.NewCoach();
        user.IssuePasswordToken("old", PasswordTokenPurpose.Welcome, Now);
        user.IssuePasswordToken("new", PasswordTokenPurpose.Reset, Now.AddMinutes(1));

        user.PasswordTokens.ShouldHaveSingleItem().TokenHash.ShouldBe("new");
        user.SetPasswordWithToken("old", "x", Now.AddMinutes(2)).Error.ShouldBe(AuthErrors.ResetTokenInvalid);
        user.SetPasswordWithToken("new", "x", Now.AddMinutes(2)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Changing_or_resetting_the_password_and_deactivation_drop_a_pending_link()
    {
        var changed = UserTests.NewCoach();
        changed.IssuePasswordToken("a", PasswordTokenPurpose.Welcome, Now);
        changed.ChangePassword("own", "r", Now);
        changed.PasswordTokens.ShouldBeEmpty();

        var reset = UserTests.NewCoach();
        reset.IssuePasswordToken("b", PasswordTokenPurpose.Reset, Now);
        reset.ResetPassword("temporary", Now);
        reset.PasswordTokens.ShouldBeEmpty();

        var deactivated = UserTests.NewCoach();
        deactivated.IssuePasswordToken("c", PasswordTokenPurpose.Reset, Now);
        deactivated.Deactivate(Now);
        deactivated.PasswordTokens.ShouldBeEmpty();
        deactivated.IssuePasswordToken("d", PasswordTokenPurpose.Reset, Now).Error.ShouldBe(AuthErrors.AccountInactive);
        deactivated.SetPasswordWithToken("c", "x", Now).Error.ShouldBe(AuthErrors.ResetTokenInvalid);
    }

    [Fact]
    public void Reset_requests_of_one_account_are_accepted_once_per_cooldown_and_never_for_a_deactivated_one()
    {
        var user = UserTests.NewCoach();

        user.TryRequestPasswordReset(Now).ShouldBeTrue();
        user.TryRequestPasswordReset(Now.AddMinutes(1)).ShouldBeFalse();
        user.TryRequestPasswordReset(Now + User.PasswordResetRequestCooldown).ShouldBeTrue();

        user.Deactivate(Now);
        user.TryRequestPasswordReset(Now.AddHours(1)).ShouldBeFalse();
    }
}
