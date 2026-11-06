using SporAkademi.Domain.Identity;

namespace SporAkademi.Domain.UnitTests.Identity;

[Trait("Story", "US-03")]
public sealed class LoginLockoutTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Four_failures_do_not_lock_the_account()
    {
        var user = UserTests.NewCoach();

        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now.AddMinutes(i)).ShouldBe(AuthErrors.InvalidCredentials);
        }

        user.CheckNotLockedOut(Now.AddMinutes(4)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Fifth_failure_within_15_minutes_locks_for_15_minutes()
    {
        var user = UserTests.NewCoach();
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now.AddMinutes(i));
        }

        var fifth = Now.AddMinutes(14).AddSeconds(59);
        user.RecordFailedLogin(fifth).ShouldBe(AuthErrors.AccountLocked);

        user.LockoutEndsAt.ShouldBe(fifth.AddMinutes(15));
        user.CheckNotLockedOut(fifth.AddMinutes(15).AddTicks(-1)).Error.ShouldBe(AuthErrors.AccountLocked);
        user.CheckNotLockedOut(fifth.AddMinutes(15)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Failures_older_than_15_minutes_start_a_new_window()
    {
        var user = UserTests.NewCoach();
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now.AddMinutes(i));
        }

        // Exactly 15 minutes after the first failure the window has closed.
        user.RecordFailedLogin(Now.AddMinutes(15)).ShouldBe(AuthErrors.InvalidCredentials);

        user.FailedLoginCount.ShouldBe(1);
        user.LockoutEndsAt.ShouldBeNull();
    }

    [Fact]
    public void Successful_login_clears_the_failure_count()
    {
        var user = UserTests.NewCoach();
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now);
        }

        user.CompleteLogin("token-hash", Now).IsSuccess.ShouldBeTrue();

        user.FailedLoginCount.ShouldBe(0);
        user.RecordFailedLogin(Now).ShouldBe(AuthErrors.InvalidCredentials);
    }

    [Fact]
    public void After_the_lock_expires_five_new_failures_are_needed()
    {
        var user = UserTests.NewCoach();
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now);
        }

        var later = Now.AddMinutes(15);
        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(later).ShouldBe(AuthErrors.InvalidCredentials);
        }

        user.RecordFailedLogin(later).ShouldBe(AuthErrors.AccountLocked);
    }

    [Fact]
    public void Deactivated_user_cannot_complete_a_login()
    {
        var user = UserTests.NewCoach();
        user.Deactivate(Now);

        user.CompleteLogin("token-hash", Now).Error.ShouldBe(AuthErrors.AccountInactive);
        user.RefreshTokens.ShouldBeEmpty();
    }
}
