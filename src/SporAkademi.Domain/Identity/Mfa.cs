using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Identity;

public readonly record struct MfaCredentialId(Guid Value) : IEntityId<MfaCredentialId>
{
    public static MfaCredentialId From(Guid value) => new(value);
}

public readonly record struct MfaRecoveryCodeId(Guid Value) : IEntityId<MfaRecoveryCodeId>
{
    public static MfaRecoveryCodeId From(Guid value) => new(value);
}

public readonly record struct MfaChallengeId(Guid Value) : IEntityId<MfaChallengeId>
{
    public static MfaChallengeId From(Guid value) => new(value);
}

/// <summary>What a second-step challenge token is good for (P5-01, DEC-601).</summary>
public enum MfaChallengePurpose
{
    /// <summary>The user has MFA: the token plus a code (or a recovery code) completes the login.</summary>
    Verify,

    /// <summary>A role that must have MFA does not yet: the token plus a confirmed enrolment completes the login.</summary>
    Enroll,
}

/// <summary>What happened to a user's MFA, for the notification e-mail (P5-01, DEC-607).</summary>
public enum MfaNotice
{
    Enabled,
    Disabled,

    /// <summary>A Platform Admin removed it (a lost phone).</summary>
    Reset,
    RecoveryCodeUsed,
}

/// <summary>
/// Why a Platform Admin reset someone's MFA. A fixed list, so the audit entry never carries free text (DEC-533).
/// </summary>
public enum MfaResetReason
{
    LostDevice,
    SuspectedCompromise,
    Other,
}

/// <summary>MFA of an account changed in a way its owner is told about (P5-01, DEC-607); the e-mail goes out after commit.</summary>
public sealed record MfaNoticeRaised(UserId UserId, MfaNotice Notice) : IDomainEvent;

/// <summary>
/// The TOTP secret of a <see cref="User"/> (P5-01). It exists as soon as an enrolment starts (<see cref="ConfirmedAt"/>
/// still empty) and counts only once a valid code confirmed it. The secret itself is stored encrypted (DEC-602); the
/// aggregate never sees it in clear.
/// </summary>
public sealed class MfaCredential : Entity<MfaCredentialId>
{
    public const int CiphertextMaxLength = 256;

    internal MfaCredential(UserId userId, string secretCiphertext, DateTimeOffset now)
        : base(EntityId.New<MfaCredentialId>())
    {
        UserId = userId;
        SecretCiphertext = secretCiphertext;
        CreatedAt = now;
    }

    // For EF Core materialization only.
    private MfaCredential()
        : base(default) => SecretCiphertext = null!;

    public UserId UserId { get; private set; }

    public string SecretCiphertext { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>The newest time step a code was accepted for; that step and every earlier one is refused afterwards (replay).</summary>
    public long? LastUsedStep { get; private set; }

    public bool IsConfirmed => ConfirmedAt is not null;

    internal void Restart(string secretCiphertext, DateTimeOffset now)
    {
        SecretCiphertext = secretCiphertext;
        CreatedAt = now;
        LastUsedStep = null;
    }

    internal bool TryAcceptStep(long step)
    {
        if (LastUsedStep is { } last && step <= last)
        {
            return false;
        }

        LastUsedStep = step;
        return true;
    }

    internal void Confirm(long step, DateTimeOffset now)
    {
        ConfirmedAt = now;
        LastUsedStep = step;
    }
}

/// <summary>A single-use recovery code; only a hash is stored (P5-01, DEC-603).</summary>
public sealed class MfaRecoveryCode : Entity<MfaRecoveryCodeId>
{
    public const int HashLength = 64;

    internal MfaRecoveryCode(UserId userId, string codeHash)
        : base(EntityId.New<MfaRecoveryCodeId>())
    {
        UserId = userId;
        CodeHash = codeHash;
    }

    // For EF Core materialization only.
    private MfaRecoveryCode()
        : base(default) => CodeHash = null!;

    public UserId UserId { get; private set; }

    public string CodeHash { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    internal void Use(DateTimeOffset now) => UsedAt = now;
}

/// <summary>
/// The second step of a login (P5-01, DEC-601): a short-lived, single-use, attempt-limited token handed out after the
/// password was right. Like a refresh token only its SHA-256 hash is stored. A user has at most one: a new login
/// replaces it, and using it up, a password change, a reset and deactivation remove it.
/// </summary>
public sealed class MfaChallenge : Entity<MfaChallengeId>
{
    public const int TokenHashLength = 64;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    internal MfaChallenge(UserId userId, string tokenHash, MfaChallengePurpose purpose, DateTimeOffset now)
        : base(EntityId.New<MfaChallengeId>())
    {
        UserId = userId;
        TokenHash = tokenHash;
        Purpose = purpose;
        IssuedAt = now;
        ExpiresAt = now + Lifetime;
    }

    // For EF Core materialization only.
    private MfaChallenge()
        : base(default) => TokenHash = null!;

    public UserId UserId { get; private set; }

    public string TokenHash { get; private set; }

    public MfaChallengePurpose Purpose { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public int FailedAttempts { get; private set; }

    public bool IsUsable(DateTimeOffset now) => now < ExpiresAt && FailedAttempts < MaxFailedAttempts;

    internal void RecordFailure() => FailedAttempts++;
}

/// <summary>Second factor errors (appendix E, Identity; P5-01).</summary>
public static class MfaErrors
{
    /// <summary>The challenge token is unknown, expired, used up or out of attempts; the user signs in again.</summary>
    public static readonly Error ChallengeInvalid = Error.Unauthorized("Mfa.ChallengeInvalid", "The sign-in step has expired; sign in again.");

    /// <summary>A wrong, malformed or already used code (a recovery code that was used counts as wrong).</summary>
    public static readonly Error CodeInvalid = Error.BusinessRule("Mfa.CodeInvalid", "The verification code is wrong.");

    /// <summary>The stored secret cannot be decrypted (the encryption key changed): a configuration fault, not a wrong code.</summary>
    public static readonly Error SecretUnreadable = Error.BusinessRule("Mfa.SecretUnreadable", "The two-step verification secret cannot be read; ask a Platform Admin to reset it.");

    public static readonly Error AlreadyEnabled = Error.Conflict("Mfa.AlreadyEnabled", "Two-step verification is already on.");
    public static readonly Error NotEnabled = Error.BusinessRule("Mfa.NotEnabled", "Two-step verification is not on.");
    public static readonly Error EnrollmentNotStarted = Error.BusinessRule("Mfa.EnrollmentNotStarted", "Start the setup first.");
    public static readonly Error NotAvailableForRole = Error.Forbidden("Mfa.NotAvailableForRole", "Two-step verification is not available for this role.");
    public static readonly Error RequiredForRole = Error.BusinessRule("Mfa.RequiredForRole", "Two-step verification is mandatory for this role and cannot be turned off.");
    public static readonly Error CannotResetOwn = Error.BusinessRule("Mfa.CannotResetOwn", "You cannot reset your own two-step verification.");
}
