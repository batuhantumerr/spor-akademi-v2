using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Identity;

[Trait("Story", "P5-01")]
public sealed class UserMfaTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    private static User NewAdmin() =>
        User.Create(EntityId.New<SchoolId>(), Email.Create("admin@example.com").Value, "hash", UserRole.SchoolAdmin, "Deniz", "Kaya", null).Value;

    private static string[] Hashes() => [.. Enumerable.Range(0, User.RecoveryCodeCount).Select(i => $"hash-{i}")];

    private static User Enrolled()
    {
        var user = NewAdmin();
        user.BeginMfaEnrollment("cipher", Now).IsSuccess.ShouldBeTrue();
        user.ConfirmMfaEnrollment(100, Hashes(), Now).IsSuccess.ShouldBeTrue();
        user.ClearDomainEvents();
        return user;
    }

    [Fact]
    public void A_pending_enrolment_does_not_turn_mfa_on_and_can_restart()
    {
        var user = NewAdmin();

        user.BeginMfaEnrollment("first", Now).IsSuccess.ShouldBeTrue();
        user.BeginMfaEnrollment("second", Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        user.MfaEnabled.ShouldBeFalse();
        user.MfaCredential!.SecretCiphertext.ShouldBe("second");
        user.AcceptMfaStep(5).Error.ShouldBe(MfaErrors.CodeInvalid);
    }

    [Fact]
    public void Confirming_turns_mfa_on_stores_the_recovery_codes_and_raises_a_notice()
    {
        var user = NewAdmin();
        user.BeginMfaEnrollment("cipher", Now);

        user.ConfirmMfaEnrollment(100, Hashes(), Now).IsSuccess.ShouldBeTrue();

        user.MfaEnabled.ShouldBeTrue();
        user.RecoveryCodesRemaining.ShouldBe(10);
        user.DomainEvents.ShouldContain(new MfaNoticeRaised(user.Id, MfaNotice.Enabled));
        user.BeginMfaEnrollment("again", Now).Error.ShouldBe(MfaErrors.AlreadyEnabled);
    }

    [Fact]
    public void Confirming_without_starting_fails()
    {
        NewAdmin().ConfirmMfaEnrollment(100, Hashes(), Now).Error.ShouldBe(MfaErrors.EnrollmentNotStarted);
    }

    [Theory]
    [InlineData(UserRole.Coach)]
    public void Coaches_cannot_enrol(UserRole role)
    {
        var user = User.Create(EntityId.New<SchoolId>(), Email.Create("coach@example.com").Value, "hash", role, "Ayşe", "Yılmaz", null).Value;

        user.BeginMfaEnrollment("cipher", Now).Error.ShouldBe(MfaErrors.NotAvailableForRole);
    }

    [Fact]
    public void A_code_is_good_once_and_older_steps_are_refused_too()
    {
        var user = Enrolled(); // confirmed with step 100

        user.AcceptMfaStep(100).Error.ShouldBe(MfaErrors.CodeInvalid);
        user.AcceptMfaStep(99).Error.ShouldBe(MfaErrors.CodeInvalid);
        user.AcceptMfaStep(101).IsSuccess.ShouldBeTrue();
        user.AcceptMfaStep(101).Error.ShouldBe(MfaErrors.CodeInvalid);
    }

    [Fact]
    public void Turning_mfa_on_or_off_ends_every_session()
    {
        var user = NewAdmin();
        user.CompleteLogin("stolen-session", Now);
        user.BeginMfaEnrollment("cipher", Now);

        user.ConfirmMfaEnrollment(100, Hashes(), Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);

        user.CompleteLogin("second-session", Now.AddMinutes(2));
        user.DisableMfa(Now.AddMinutes(3)).IsSuccess.ShouldBeTrue();
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public void A_reset_clears_an_enrolment_that_was_never_confirmed()
    {
        var user = NewAdmin();
        user.BeginMfaEnrollment("cipher", Now);
        user.ClearDomainEvents();

        user.ResetMfa(Now).IsSuccess.ShouldBeTrue();

        user.MfaCredential.ShouldBeNull();
        user.DomainEvents.ShouldBeEmpty(); // nothing was on, so nobody is told
        user.ResetMfa(Now).Error.ShouldBe(MfaErrors.NotEnabled);
    }

    [Fact]
    public void A_recovery_code_works_once()
    {
        var user = Enrolled();

        user.UseRecoveryCode("hash-3", Now).IsSuccess.ShouldBeTrue();

        user.RecoveryCodesRemaining.ShouldBe(9);
        user.DomainEvents.ShouldContain(new MfaNoticeRaised(user.Id, MfaNotice.RecoveryCodeUsed));
        user.UseRecoveryCode("hash-3", Now).Error.ShouldBe(MfaErrors.CodeInvalid);
        user.UseRecoveryCode("unknown", Now).Error.ShouldBe(MfaErrors.CodeInvalid);
    }

    [Fact]
    public void Disabling_removes_everything_and_raises_a_notice()
    {
        var user = Enrolled();

        user.DisableMfa(Now).IsSuccess.ShouldBeTrue();

        user.MfaEnabled.ShouldBeFalse();
        user.MfaCredential.ShouldBeNull();
        user.RecoveryCodes.ShouldBeEmpty();
        user.DomainEvents.ShouldContain(new MfaNoticeRaised(user.Id, MfaNotice.Disabled));
        user.DisableMfa(Now).Error.ShouldBe(MfaErrors.NotEnabled);
    }

    [Fact]
    public void A_reset_removes_everything_ends_the_sessions_and_raises_a_notice()
    {
        var user = Enrolled();
        user.CompleteLogin("session-hash", Now);

        user.ResetMfa(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        user.MfaEnabled.ShouldBeFalse();
        user.RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
        user.DomainEvents.ShouldContain(new MfaNoticeRaised(user.Id, MfaNotice.Reset));
        user.ResetMfa(Now).Error.ShouldBe(MfaErrors.NotEnabled);
    }

    [Fact]
    public void A_challenge_lives_five_minutes_and_a_newer_login_replaces_it()
    {
        var user = Enrolled();
        user.IssueMfaChallenge("first", MfaChallengePurpose.Verify, Now);

        user.FindMfaChallenge("first", MfaChallengePurpose.Verify, Now.AddMinutes(4)).IsSuccess.ShouldBeTrue();
        user.FindMfaChallenge("first", MfaChallengePurpose.Verify, Now.AddMinutes(5)).Error.ShouldBe(MfaErrors.ChallengeInvalid);
        user.FindMfaChallenge("first", MfaChallengePurpose.Enroll, Now).Error.ShouldBe(MfaErrors.ChallengeInvalid);

        user.IssueMfaChallenge("second", MfaChallengePurpose.Verify, Now);
        user.FindMfaChallenge("first", MfaChallengePurpose.Verify, Now).Error.ShouldBe(MfaErrors.ChallengeInvalid);
        user.MfaChallenges.Count.ShouldBe(1);
    }

    [Fact]
    public void Five_wrong_codes_use_up_the_challenge_and_count_towards_the_account_lockout()
    {
        var user = Enrolled();
        var challenge = user.IssueMfaChallenge("token", MfaChallengePurpose.Verify, Now);

        for (var i = 0; i < 4; i++)
        {
            user.RecordMfaFailure(challenge, Now).ShouldBe(MfaErrors.CodeInvalid);
        }

        user.FindMfaChallenge("token", MfaChallengePurpose.Verify, Now).IsSuccess.ShouldBeTrue();
        user.RecordMfaFailure(challenge, Now).ShouldBe(AuthErrors.AccountLocked); // the fifth failure also locks the account (DEC-010)
        user.CheckNotLockedOut(Now).IsFailure.ShouldBeTrue();
        user.FindMfaChallenge("token", MfaChallengePurpose.Verify, Now.AddMinutes(1)).Error.ShouldBe(MfaErrors.ChallengeInvalid);
    }

    [Fact]
    public void A_password_change_ends_a_half_finished_login()
    {
        var user = Enrolled();
        user.IssueMfaChallenge("token", MfaChallengePurpose.Verify, Now);

        user.ChangePassword("new-hash", "session-hash", Now);

        user.MfaChallenges.ShouldBeEmpty();
    }
}
