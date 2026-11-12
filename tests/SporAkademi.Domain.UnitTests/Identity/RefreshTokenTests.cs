using SporAkademi.Domain.Identity;

namespace SporAkademi.Domain.UnitTests.Identity;

[Trait("Story", "US-04")]
public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    private static (User User, RefreshToken Token) LoggedIn()
    {
        var user = UserTests.NewCoach();
        var token = user.CompleteLogin("h1", Now).Value;
        return (user, token);
    }

    [Fact]
    public void Login_issues_a_seven_day_token_in_a_new_thirty_day_family()
    {
        var (_, token) = LoggedIn();

        token.TokenHash.ShouldBe("h1");
        token.IssuedAt.ShouldBe(Now);
        token.ExpiresAt.ShouldBe(Now.AddDays(7));
        token.FamilyExpiresAt.ShouldBe(Now.AddDays(30));
        token.IsActive(Now).ShouldBeTrue();
    }

    [Fact]
    public void Rotation_replaces_the_token_within_the_same_family()
    {
        var (user, first) = LoggedIn();
        var later = Now.AddHours(1);

        var second = user.RotateRefreshToken("h1", "h2", later).Value;

        second.FamilyId.ShouldBe(first.FamilyId);
        second.ExpiresAt.ShouldBe(later.AddDays(7));
        second.FamilyExpiresAt.ShouldBe(first.FamilyExpiresAt);
        first.RevokedAt.ShouldBe(later);
        first.ReplacedByTokenId.ShouldBe(second.Id);
        first.IsActive(later).ShouldBeFalse();
        second.IsActive(later).ShouldBeTrue();
    }

    [Fact]
    public void Reusing_a_rotated_token_revokes_every_token_of_the_user()
    {
        var (user, _) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;
        var rotated = user.RotateRefreshToken("h1", "h2", Now.AddMinutes(20)).Value;

        user.RotateRefreshToken("h1", "h3", Now.AddMinutes(30)).Error.ShouldBe(AuthErrors.SessionExpired);

        rotated.IsActive(Now.AddMinutes(30)).ShouldBeFalse();
        otherDevice.IsActive(Now.AddMinutes(30)).ShouldBeFalse();
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public void Replaying_a_rotated_token_within_the_grace_window_reissues_in_the_same_family()
    {
        // DEC-446: the response carrying h2 was lost, so the client presents h1 again a few seconds later.
        var (user, first) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;
        var lost = user.RotateRefreshToken("h1", "h2", Now.AddMinutes(20)).Value;
        var retry = Now.AddMinutes(20).AddSeconds(10);

        var reissued = user.RotateRefreshToken("h1", "h3", retry).Value;

        reissued.TokenHash.ShouldBe("h3");
        reissued.FamilyId.ShouldBe(first.FamilyId);
        reissued.FamilyExpiresAt.ShouldBe(first.FamilyExpiresAt);
        reissued.IsActive(retry).ShouldBeTrue();
        lost.IsActive(retry).ShouldBeFalse();
        lost.ReplacedByTokenId.ShouldBe(reissued.Id);
        first.ReplacedByTokenId.ShouldBe(reissued.Id);
        first.RevokedAt.ShouldBe(Now.AddMinutes(20));
        otherDevice.IsActive(retry).ShouldBeTrue();
    }

    [Fact]
    public void A_second_lost_response_within_the_original_window_is_still_a_retry()
    {
        var (user, _) = LoggedIn();
        user.RotateRefreshToken("h1", "h2", Now).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h1", "h3", Now.AddSeconds(5)).IsSuccess.ShouldBeTrue();

        var again = user.RotateRefreshToken("h1", "h4", Now.AddSeconds(29));

        again.IsSuccess.ShouldBeTrue();
        user.RefreshTokens.Count(t => t.IsActive(Now.AddSeconds(29))).ShouldBe(1);
    }

    [Fact]
    public void The_grace_window_is_measured_from_the_original_rotation_and_ends_after_thirty_seconds()
    {
        var (user, _) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;
        user.RotateRefreshToken("h1", "h2", Now).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h1", "h3", Now.AddSeconds(20)).IsSuccess.ShouldBeTrue();

        // Retrying at second 20 did not move the window: second 30 is past it.
        user.RotateRefreshToken("h1", "h4", Now + User.RefreshTokenReuseGraceWindow).Error.ShouldBe(AuthErrors.SessionExpired);

        otherDevice.RevokedAt.ShouldNotBeNull();
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public void Replaying_a_token_whose_successor_was_already_used_revokes_everything_even_within_the_window()
    {
        // The successor was rotated, so whoever holds the chain received it; a second holder of the old token is theft.
        var (user, _) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;
        user.RotateRefreshToken("h1", "h2", Now).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h2", "h3", Now.AddSeconds(2)).IsSuccess.ShouldBeTrue();

        user.RotateRefreshToken("h1", "h4", Now.AddSeconds(5)).Error.ShouldBe(AuthErrors.SessionExpired);

        otherDevice.IsActive(Now.AddSeconds(5)).ShouldBeFalse();
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public void Replaying_a_rotated_token_of_a_logged_out_family_gets_no_grace()
    {
        var (user, _) = LoggedIn();
        user.RotateRefreshToken("h1", "h2", Now).IsSuccess.ShouldBeTrue();
        user.RevokeRefreshToken("h2", Now.AddSeconds(1));

        user.RotateRefreshToken("h1", "h3", Now.AddSeconds(2)).Error.ShouldBe(AuthErrors.SessionExpired);

        user.RefreshTokens.ShouldNotContain(t => t.TokenHash == "h3");
    }

    [Fact]
    public void Replaying_within_the_window_after_deactivation_gets_no_grace()
    {
        var (user, _) = LoggedIn();
        user.RotateRefreshToken("h1", "h2", Now).IsSuccess.ShouldBeTrue();
        user.Deactivate(Now.AddSeconds(1)).IsSuccess.ShouldBeTrue();

        user.RotateRefreshToken("h1", "h3", Now.AddSeconds(2)).Error.ShouldBe(AuthErrors.SessionExpired);

        user.RefreshTokens.ShouldNotContain(t => t.TokenHash == "h3");
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public void Replaying_within_the_window_after_a_password_change_gets_no_grace()
    {
        var (user, _) = LoggedIn();
        user.RotateRefreshToken("h1", "h2", Now).IsSuccess.ShouldBeTrue();
        var afterChange = user.ChangePassword("new-password-hash", "fresh", Now.AddSeconds(1)).Value;

        user.RotateRefreshToken("h1", "h3", Now.AddSeconds(2)).Error.ShouldBe(AuthErrors.SessionExpired);

        user.RefreshTokens.ShouldNotContain(t => t.TokenHash == "h3");
        // A reuse of a rotated token still counts as theft: the new session ends too.
        afterChange.IsActive(Now.AddSeconds(2)).ShouldBeFalse();
    }

    [Fact]
    public void Replaying_within_the_window_after_the_family_expired_gets_no_grace()
    {
        var (user, first) = LoggedIn();
        var rotatedAt = first.FamilyExpiresAt.AddSeconds(-10);
        user.RotateRefreshToken("h1", "h2", Now.AddDays(6)).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h2", "h3", Now.AddDays(12)).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h3", "h4", Now.AddDays(18)).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h4", "h5", Now.AddDays(24)).IsSuccess.ShouldBeTrue();
        user.RotateRefreshToken("h5", "h6", rotatedAt).IsSuccess.ShouldBeTrue();

        // Ten seconds after the rotation the family (and so the unused head h6) has reached its 30-day end.
        user.RotateRefreshToken("h5", "h7", rotatedAt.AddSeconds(10)).Error.ShouldBe(AuthErrors.SessionExpired);

        user.RefreshTokens.ShouldNotContain(t => t.TokenHash == "h7");
    }

    [Fact]
    public void A_token_revoked_by_logout_is_rejected_without_revoking_other_sessions()
    {
        var (user, _) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;
        user.RevokeRefreshToken("h1", Now);

        user.RotateRefreshToken("h1", "h2", Now.AddMinutes(1)).Error.ShouldBe(AuthErrors.SessionExpired);

        otherDevice.IsActive(Now.AddMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void Expired_or_unknown_tokens_are_rejected()
    {
        var (user, _) = LoggedIn();

        user.RotateRefreshToken("unknown", "h2", Now).Error.ShouldBe(AuthErrors.SessionExpired);
        user.RotateRefreshToken("h1", "h2", Now.AddDays(7)).Error.ShouldBe(AuthErrors.SessionExpired);
    }

    [Fact]
    public void A_token_used_just_before_expiry_still_rotates()
    {
        var (user, _) = LoggedIn();

        user.RotateRefreshToken("h1", "h2", Now.AddDays(7).AddTicks(-1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_family_never_outlives_thirty_days_from_the_first_login()
    {
        var (user, _) = LoggedIn();
        var hash = "h1";
        var at = Now;
        for (var i = 2; i <= 5; i++)
        {
            at = at.AddDays(6);
            var next = $"h{i}";
            user.RotateRefreshToken(hash, next, at).IsSuccess.ShouldBeTrue();
            hash = next;
        }

        // Day 24: the new token's 7 days would pass day 30, so it is capped.
        var last = user.RefreshTokens.Single(t => t.TokenHash == hash);
        last.ExpiresAt.ShouldBe(Now.AddDays(30));

        user.RotateRefreshToken(hash, "h6", Now.AddDays(30)).Error.ShouldBe(AuthErrors.SessionExpired);
    }

    [Fact]
    public void Logout_revokes_only_the_presented_session_and_is_idempotent()
    {
        var (user, token) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;

        user.RevokeRefreshToken("h1", Now);
        user.RevokeRefreshToken("h1", Now.AddMinutes(1));

        token.RevokedAt.ShouldBe(Now);
        otherDevice.IsActive(Now).ShouldBeTrue();
    }

    [Fact]
    public void Logout_with_an_already_rotated_token_still_ends_its_whole_family()
    {
        // Two tabs share one cookie jar; if a stale token reaches logout (a race with a rotation), the session it
        // belongs to must end too, not stay alive through its newer token.
        var (user, _) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;
        var newest = user.RotateRefreshToken("h1", "h2", Now.AddMinutes(5)).Value;

        user.RevokeRefreshToken("h1", Now.AddMinutes(6));

        newest.IsActive(Now.AddMinutes(6)).ShouldBeFalse();
        user.RotateRefreshToken("h2", "h3", Now.AddMinutes(7)).Error.ShouldBe(AuthErrors.SessionExpired);
        otherDevice.IsActive(Now.AddMinutes(7)).ShouldBeTrue();
    }

    [Fact]
    public void Tokens_of_expired_families_are_pruned_on_the_next_login()
    {
        var (user, _) = LoggedIn();

        user.CompleteLogin("fresh", Now.AddDays(30)).IsSuccess.ShouldBeTrue();

        user.RefreshTokens.Select(t => t.TokenHash).ShouldBe(["fresh"]);
    }

    [Fact]
    public void Deactivated_user_cannot_rotate()
    {
        var (user, _) = LoggedIn();
        user.Deactivate(Now);

        user.RotateRefreshToken("h1", "h2", Now).Error.ShouldBe(AuthErrors.SessionExpired);
    }
}
