using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Identity;

/// <summary>
/// The second factor of a <see cref="User"/> (P5-01, DEC-600 – DEC-609): a TOTP secret, single-use recovery codes and
/// the short-lived challenge between the password and the second step of a login. The aggregate decides the rules; the
/// application layer does the cryptography (decrypting the secret, matching codes) and passes the result in.
/// </summary>
public sealed partial class User
{
    public const int RecoveryCodeCount = 10;

    private readonly List<MfaRecoveryCode> _recoveryCodes = [];
    private readonly List<MfaChallenge> _mfaChallenges = [];
    private MfaCredential? _mfaCredential;

    /// <summary>True once an enrolment was confirmed with a valid code; a pending enrolment does not count.</summary>
    public bool MfaEnabled { get; private set; }

    /// <summary>The secret of a confirmed or pending enrolment, if any.</summary>
    public MfaCredential? MfaCredential => _mfaCredential;

    public IReadOnlyCollection<MfaRecoveryCode> RecoveryCodes => _recoveryCodes.AsReadOnly();

    public IReadOnlyCollection<MfaChallenge> MfaChallenges => _mfaChallenges.AsReadOnly();

    public int RecoveryCodesRemaining => _recoveryCodes.Count(c => c.UsedAt is null);

    /// <summary>Staff who open the admin panel with school-wide or platform-wide reach; coaches and parents do not use it (DEC-604).</summary>
    public static bool MayUseMfa(UserRole role) => role is UserRole.PlatformAdmin or UserRole.SchoolAdmin;

    /// <summary>
    /// Starts (or restarts) an enrolment with a new encrypted secret. An earlier unconfirmed secret is replaced; a
    /// confirmed one is not: the user turns MFA off first.
    /// </summary>
    public Result BeginMfaEnrollment(string secretCiphertext, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretCiphertext);
        if (Status != UserStatus.Active)
        {
            return Result.Failure(AuthErrors.AccountInactive);
        }

        if (!MayUseMfa(Role))
        {
            return Result.Failure(MfaErrors.NotAvailableForRole);
        }

        if (MfaEnabled)
        {
            return Result.Failure(MfaErrors.AlreadyEnabled);
        }

        if (_mfaCredential is null)
        {
            _mfaCredential = new MfaCredential(Id, secretCiphertext, now);
        }
        else
        {
            _mfaCredential.Restart(secretCiphertext, now);
        }

        return Result.Success();
    }

    /// <summary>
    /// Confirms the pending enrolment: <paramref name="step"/> is the time step of the valid code the user typed, and
    /// <paramref name="recoveryCodeHashes"/> the hashes of the <see cref="RecoveryCodeCount"/> codes shown to them once.
    /// </summary>
    public Result ConfirmMfaEnrollment(long step, IReadOnlyCollection<string> recoveryCodeHashes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodeHashes);
        if (MfaEnabled)
        {
            return Result.Failure(MfaErrors.AlreadyEnabled);
        }

        if (_mfaCredential is null)
        {
            return Result.Failure(MfaErrors.EnrollmentNotStarted);
        }

        _mfaCredential.Confirm(step, now);
        // A session stolen earlier with the password alone must not outlive the moment MFA is turned on.
        RevokeAllRefreshTokens(now);
        _recoveryCodes.Clear();
        _recoveryCodes.AddRange(recoveryCodeHashes.Select(hash => new MfaRecoveryCode(Id, hash)));
        MfaEnabled = true;
        Raise(new MfaNoticeRaised(Id, MfaNotice.Enabled));
        return Result.Success();
    }

    /// <summary>
    /// Accepts the time step of a valid code unless that step, or a later one, was accepted before: a code is good once
    /// (replay protection).
    /// </summary>
    public Result AcceptMfaStep(long step) =>
        _mfaCredential is { IsConfirmed: true } credential && credential.TryAcceptStep(step)
            ? Result.Success()
            : Result.Failure(MfaErrors.CodeInvalid);

    /// <summary>Uses up the recovery code with this hash. Raises a notice: a recovery code is rare and worth knowing about.</summary>
    public Result UseRecoveryCode(string codeHash, DateTimeOffset now)
    {
        var code = MfaEnabled
            ? _recoveryCodes.Find(c => c.UsedAt is null && string.Equals(c.CodeHash, codeHash, StringComparison.Ordinal))
            : null;
        if (code is null)
        {
            return Result.Failure(MfaErrors.CodeInvalid);
        }

        code.Use(now);
        Raise(new MfaNoticeRaised(Id, MfaNotice.RecoveryCodeUsed));
        return Result.Success();
    }

    /// <summary>
    /// The signed-in owner replaced all recovery codes (V1-03, DEC-687; the caller checked a current code): the old ones,
    /// used or not, are gone. Sessions stay: the owner is in one, and nothing about the sign-in changed.
    /// </summary>
    public Result RegenerateMfaRecoveryCodes(IReadOnlyCollection<string> recoveryCodeHashes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodeHashes);
        if (!MfaEnabled)
        {
            return Result.Failure(MfaErrors.NotEnabled);
        }

        _recoveryCodes.Clear();
        _recoveryCodes.AddRange(recoveryCodeHashes.Select(hash => new MfaRecoveryCode(Id, hash)));
        Raise(new MfaNoticeRaised(Id, MfaNotice.RecoveryCodesRegenerated));
        return Result.Success();
    }

    /// <summary>The same TOTP secret, encrypted under another key (V1-03, DEC-685); a quiet maintenance change.</summary>
    public Result RewriteMfaSecret(string secretCiphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretCiphertext);
        if (_mfaCredential is null)
        {
            return Result.Failure(MfaErrors.EnrollmentNotStarted);
        }

        _mfaCredential.ReplaceCiphertext(secretCiphertext);
        return Result.Success();
    }

    /// <summary>The user turned MFA off (the caller checked a current code and that the role may do without it).</summary>
    public Result DisableMfa(DateTimeOffset now)
    {
        if (!MfaEnabled)
        {
            return Result.Failure(MfaErrors.NotEnabled);
        }

        RemoveMfa();
        // Turning a protection off ends every session: the owner signs in again, someone who only had a session does not stay.
        RevokeAllRefreshTokens(now);
        Raise(new MfaNoticeRaised(Id, MfaNotice.Disabled));
        return Result.Success();
    }

    /// <summary>
    /// A Platform Admin removed the user's MFA (a lost phone, P5-01 DEC-605): every session ends, so the next login
    /// starts over, with an enrolment where the role requires one.
    /// </summary>
    public Result ResetMfa(DateTimeOffset now)
    {
        if (!MfaEnabled)
        {
            // An enrolment that was never confirmed is cleared quietly; with nothing at all there is nothing to reset.
            if (_mfaCredential is null)
            {
                return Result.Failure(MfaErrors.NotEnabled);
            }

            RemoveMfa();
            return Result.Success();
        }

        RemoveMfa();
        RevokeAllRefreshTokens(now);
        Raise(new MfaNoticeRaised(Id, MfaNotice.Reset));
        return Result.Success();
    }

    /// <summary>Issues the challenge of a login whose password was right; any earlier challenge of the user ends.</summary>
    public MfaChallenge IssueMfaChallenge(string tokenHash, MfaChallengePurpose purpose, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        _mfaChallenges.Clear();
        var challenge = new MfaChallenge(Id, tokenHash, purpose, now);
        _mfaChallenges.Add(challenge);
        return challenge;
    }

    /// <summary>The usable challenge with this hash and purpose; anything else is <c>Mfa.ChallengeInvalid</c>, never saying which.</summary>
    public Result<MfaChallenge> FindMfaChallenge(string tokenHash, MfaChallengePurpose purpose, DateTimeOffset now)
    {
        var challenge = _mfaChallenges.Find(c => string.Equals(c.TokenHash, tokenHash, StringComparison.Ordinal));
        return challenge is not null && challenge.Purpose == purpose && challenge.IsUsable(now)
            ? Result.Success(challenge)
            : Result.Failure<MfaChallenge>(MfaErrors.ChallengeInvalid);
    }

    /// <summary>
    /// Counts a wrong code: against the challenge (five per challenge) and, like a wrong password, against the account
    /// lockout (DEC-010). Returns the error to report.
    /// </summary>
    public Error RecordMfaFailure(MfaChallenge? challenge, DateTimeOffset now)
    {
        challenge?.RecordFailure();
        return RecordFailedLogin(now) == AuthErrors.AccountLocked ? AuthErrors.AccountLocked : MfaErrors.CodeInvalid;
    }

    /// <summary>The challenge was answered: it is used up.</summary>
    public void CompleteMfaChallenge(MfaChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _mfaChallenges.Remove(challenge);
    }

    private void RemoveMfa()
    {
        _mfaCredential = null;
        _recoveryCodes.Clear();
        _mfaChallenges.Clear();
        MfaEnabled = false;
    }
}
