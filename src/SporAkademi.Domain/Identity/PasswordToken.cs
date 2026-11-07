using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Identity;

public readonly record struct PasswordTokenId(Guid Value) : IEntityId<PasswordTokenId>
{
    public static PasswordTokenId From(Guid value) => new(value);
}

/// <summary>Why a set-password link was issued (DEC-351, DEC-352).</summary>
public enum PasswordTokenPurpose
{
    /// <summary>Self-service "forgot password" (P2-F4).</summary>
    Reset,

    /// <summary>A new account's welcome e-mail: the user sets a password without the temporary one.</summary>
    Welcome,
}

/// <summary>
/// A single-use, short-lived set-password token of a <see cref="User"/> (P2-F4, DEC-352). Like a refresh token only
/// its SHA-256 hash is stored; the token itself exists only in the e-mail. A user has at most one: issuing a new one,
/// using it, changing or resetting the password and deactivation remove it.
/// </summary>
public sealed class PasswordToken : Entity<PasswordTokenId>
{
    public const int TokenHashLength = 64;

    private PasswordToken(PasswordTokenId id, UserId userId, string tokenHash, PasswordTokenPurpose purpose, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        Purpose = purpose;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    // For EF Core materialization only.
    private PasswordToken()
        : base(default) => TokenHash = null!;

    public UserId UserId { get; private set; }

    public string TokenHash { get; private set; }

    public PasswordTokenPurpose Purpose { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsValid(DateTimeOffset now) => now < ExpiresAt;

    internal static PasswordToken Issue(UserId userId, string tokenHash, PasswordTokenPurpose purpose, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        return new PasswordToken(EntityId.New<PasswordTokenId>(), userId, tokenHash, purpose, now, now + LifetimeOf(purpose));
    }

    /// <summary>DEC-352: a reset link lives 30 minutes; a welcome link 72 hours, since a new user may not read mail at once.</summary>
    public static TimeSpan LifetimeOf(PasswordTokenPurpose purpose) => purpose switch
    {
        PasswordTokenPurpose.Reset => TimeSpan.FromMinutes(30),
        PasswordTokenPurpose.Welcome => TimeSpan.FromHours(72),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
    };
}
