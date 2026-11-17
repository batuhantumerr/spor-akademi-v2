using SporAkademi.Domain.Identity;

namespace SporAkademi.Domain.UnitTests.Identity;

/// <summary>P5-02 (DEC-610): idle timeout and absolute lifetime of a session, per policy, with a fixed clock.</summary>
[Trait("Story", "P5-02")]
public sealed class SessionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 8, 0, 0, TimeSpan.Zero);

    private static (User User, RefreshToken Token) PanelLogin()
    {
        var user = UserTests.NewCoach();
        return (user, user.CompleteLogin("h1", Now, SessionPolicy.Panel).Value);
    }

    [Fact]
    public void A_panel_login_gets_a_thirty_minute_token_in_a_twelve_hour_session()
    {
        var (_, token) = PanelLogin();

        token.ExpiresAt.ShouldBe(Now.AddMinutes(30));
        token.FamilyStartedAt.ShouldBe(Now);
        token.FamilyExpiresAt.ShouldBe(Now.AddHours(12));
    }

    [Fact]
    public void A_panel_refresh_after_the_idle_timeout_is_an_expired_session_and_revokes_nothing()
    {
        var (user, _) = PanelLogin();
        var other = user.CompleteLogin("other", Now.AddMinutes(20), SessionPolicy.Panel).Value;

        var late = user.RotateRefreshToken("h1", "h2", Now.AddMinutes(30), SessionPolicy.Panel);

        late.Error.ShouldBe(AuthErrors.SessionExpired);
        other.RevokedAt.ShouldBeNull();
        user.RotateRefreshToken("other", "o2", Now.AddMinutes(40), SessionPolicy.Panel).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_panel_refresh_just_inside_the_idle_timeout_keeps_the_session_but_never_past_twelve_hours()
    {
        var (user, _) = PanelLogin();
        var hash = "h1";
        var at = Now;
        for (var i = 1; i <= 40; i++)
        {
            at = at.AddMinutes(29);
            var next = $"h{i + 1}";
            var rotated = user.RotateRefreshToken(hash, next, at, SessionPolicy.Panel);
            if (at >= Now.AddHours(12))
            {
                rotated.Error.ShouldBe(AuthErrors.SessionExpired);
                return;
            }

            rotated.IsSuccess.ShouldBeTrue();
            rotated.Value.ExpiresAt.ShouldBeLessThanOrEqualTo(Now.AddHours(12));
            hash = next;
        }

        Assert.Fail("The session outlived twelve hours.");
    }

    [Fact]
    public void A_session_of_the_old_policy_is_cut_to_the_panel_policy_at_its_next_refresh()
    {
        var user = UserTests.NewCoach();
        user.CompleteLogin("h1", Now, SessionPolicy.Standard);

        // Thirty-one minutes later the old seven-day token is still active, but the panel idle timeout is over.
        user.RotateRefreshToken("h1", "h2", Now.AddMinutes(31), SessionPolicy.Panel).Error.ShouldBe(AuthErrors.SessionExpired);

        // Another old session, refreshed in time, is capped at twelve hours from its login.
        user.CompleteLogin("g1", Now, SessionPolicy.Standard);
        var cut = user.RotateRefreshToken("g1", "g2", Now.AddMinutes(20), SessionPolicy.Panel).Value;
        cut.FamilyExpiresAt.ShouldBe(Now.AddHours(12));
        cut.ExpiresAt.ShouldBe(Now.AddMinutes(50));
    }

    [Fact]
    public void The_standard_policy_is_unchanged()
    {
        var user = UserTests.NewCoach();
        var first = user.CompleteLogin("h1", Now, SessionPolicy.Standard).Value;

        first.ExpiresAt.ShouldBe(Now.AddDays(7));
        first.FamilyExpiresAt.ShouldBe(Now.AddDays(30));
        user.RotateRefreshToken("h1", "h2", Now.AddHours(13), SessionPolicy.Standard).IsSuccess.ShouldBeTrue();
    }
}
