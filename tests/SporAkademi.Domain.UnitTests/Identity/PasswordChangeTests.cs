using SporAkademi.Domain.Identity;

namespace SporAkademi.Domain.UnitTests.Identity;

[Trait("Story", "US-05")]
public sealed class PasswordChangeTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Changing_the_password_clears_the_flag_revokes_all_tokens_and_raises_an_event()
    {
        var user = UserTests.NewCoach();
        var first = user.CompleteLogin("h1", Now).Value;
        var second = user.CompleteLogin("h2", Now).Value;
        user.RecordFailedLogin(Now);

        var session = user.ChangePassword("new-hash", "h3", Now.AddMinutes(1)).Value;

        session.IsActive(Now.AddMinutes(1)).ShouldBeTrue();
        user.PasswordHash.ShouldBe("new-hash");
        user.MustChangePassword.ShouldBeFalse();
        user.FailedLoginCount.ShouldBe(0);
        first.RevokedAt.ShouldBe(Now.AddMinutes(1));
        second.RevokedAt.ShouldBe(Now.AddMinutes(1));
        user.DomainEvents.OfType<PasswordChanged>().Single().UserId.ShouldBe(user.Id);
    }

    [Fact]
    public void Admin_reset_sets_a_temporary_password_and_revokes_all_tokens()
    {
        var user = UserTests.NewCoach();
        user.ChangePassword("own", "h0", Now);
        var token = user.CompleteLogin("h1", Now).Value;
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.ResetPassword("temporary-hash", Now.AddMinutes(1));

        user.PasswordHash.ShouldBe("temporary-hash");
        user.MustChangePassword.ShouldBeTrue();
        token.RevokedAt.ShouldBe(Now.AddMinutes(1));
        user.LockoutEndsAt.ShouldBeNull();
        user.CheckNotLockedOut(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Deactivation_revokes_all_tokens_and_raises_an_event_once()
    {
        var user = UserTests.NewCoach();
        var token = user.CompleteLogin("h1", Now).Value;

        user.Deactivate(Now).IsSuccess.ShouldBeTrue();
        user.Deactivate(Now).Error.ShouldBe(UserErrors.AlreadyInactive);

        user.Status.ShouldBe(UserStatus.Deactivated);
        token.RevokedAt.ShouldBe(Now);
        user.DomainEvents.OfType<UserDeactivated>().Count().ShouldBe(1);
    }

    [Fact]
    public void Deactivated_user_cannot_change_the_password()
    {
        var user = UserTests.NewCoach();
        user.Deactivate(Now);

        user.ChangePassword("new-hash", "h1", Now).Error.ShouldBe(AuthErrors.AccountInactive);
        user.RefreshTokens.ShouldBeEmpty();
    }
}
