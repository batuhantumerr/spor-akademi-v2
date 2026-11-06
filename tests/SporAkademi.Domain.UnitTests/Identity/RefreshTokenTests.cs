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
    public void Logout_revokes_only_the_presented_token_and_is_idempotent()
    {
        var (user, token) = LoggedIn();
        var otherDevice = user.CompleteLogin("other", Now).Value;

        user.RevokeRefreshToken("h1", Now);
        user.RevokeRefreshToken("h1", Now.AddMinutes(1));

        token.RevokedAt.ShouldBe(Now);
        otherDevice.IsActive(Now).ShouldBeTrue();
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
