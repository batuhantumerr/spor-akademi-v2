using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Identity;

public readonly record struct RefreshTokenId(Guid Value) : IEntityId<RefreshTokenId>
{
    public static RefreshTokenId From(Guid value) => new(value);
}

/// <summary>
/// One opaque refresh token of a <see cref="User"/>; only its SHA-256 hash is stored (ADR-0006).
/// Tokens that replace each other share a <see cref="FamilyId"/> and the family's absolute expiry.
/// </summary>
public sealed class RefreshToken : Entity<RefreshTokenId>
{
    public const int TokenHashLength = 64;

    private RefreshToken(RefreshTokenId id, UserId userId, string tokenHash, Guid familyId, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset familyExpiresAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        FamilyExpiresAt = familyExpiresAt;
    }

    // For EF Core materialization only.
    private RefreshToken()
        : base(default) => TokenHash = null!;

    public UserId UserId { get; private set; }

    public string TokenHash { get; private set; }

    public Guid FamilyId { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The absolute end of the session this token belongs to: 30 days after the login (NFR-SEC-05).</summary>
    public DateTimeOffset FamilyExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>
    /// Set when the token was rotated; presenting such a token again is a reuse (US-04), unless it is a lost response
    /// retried within <see cref="User.RefreshTokenReuseGraceWindow"/> (DEC-446).
    /// </summary>
    public RefreshTokenId? ReplacedByTokenId { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    internal static RefreshToken Issue(UserId userId, string tokenHash, Guid familyId, DateTimeOffset now, DateTimeOffset familyExpiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        var expiresAt = Min(now + User.RefreshTokenLifetime, familyExpiresAt);
        return new RefreshToken(EntityId.New<RefreshTokenId>(), userId, tokenHash, familyId, now, expiresAt, familyExpiresAt);
    }

    internal void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    internal void ReplaceWith(RefreshToken successor, DateTimeOffset now)
    {
        Revoke(now);
        ReplacedByTokenId = successor.Id;
    }

    /// <summary>DEC-446: a rotated token whose successor was re-issued after a lost response points at the new head.</summary>
    internal void RepointTo(RefreshToken successor) => ReplacedByTokenId = successor.Id;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
