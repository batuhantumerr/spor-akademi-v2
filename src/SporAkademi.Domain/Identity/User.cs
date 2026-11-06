using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Identity;

public readonly record struct UserId(Guid Value) : IEntityId<UserId>
{
    public static UserId From(Guid value) => new(value);
}

public enum UserRole
{
    PlatformAdmin,
    SchoolAdmin,
    Coach,
}

public enum UserStatus
{
    Active,
    Deactivated,
}

public sealed record PasswordChanged(UserId UserId) : IDomainEvent;

public sealed record UserDeactivated(UserId UserId, SchoolId? TenantId) : IDomainEvent;

/// <summary>
/// An account. Not tenant-scoped (DEC-105): the Platform Admin has no school and login looks users up
/// by e-mail across schools. Every other role belongs to exactly one school.
/// </summary>
public sealed class User : AggregateRoot<UserId>
{
    public const int NameMaxLength = 100;
    public const int PasswordHashMaxLength = 256;

    /// <summary>DEC-010: five failures within this window lock the account for <see cref="LockoutDuration"/>.</summary>
    public static readonly TimeSpan FailedLoginWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const int MaxFailedLogins = 5;

    /// <summary>NFR-SEC-05 / DEC-032: refresh tokens live 7 days; a session at most 30 days from login.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private readonly List<RefreshToken> _refreshTokens = [];

    private User(UserId id, SchoolId? tenantId, Email email, string passwordHash, UserRole role, string firstName, string lastName, PhoneNumber? phone)
        : base(id)
    {
        TenantId = tenantId;
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        FirstName = firstName;
        LastName = lastName;
        Phone = phone;
        Status = UserStatus.Active;
        MustChangePassword = true;
    }

    // For EF Core materialization only.
    private User()
        : base(default)
    {
        Email = null!;
        PasswordHash = null!;
        FirstName = null!;
        LastName = null!;
    }

    /// <summary>The user's school; <c>null</c> only for the Platform Admin.</summary>
    public SchoolId? TenantId { get; private set; }

    public Email Email { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public PhoneNumber? Phone { get; private set; }

    public UserStatus Status { get; private set; }

    /// <summary>True after creation and after an admin reset: only the password change is allowed (US-05).</summary>
    public bool MustChangePassword { get; private set; }

    public int FailedLoginCount { get; private set; }

    /// <summary>Start of the current failed-login window.</summary>
    public DateTimeOffset? FirstFailedLoginAt { get; private set; }

    public DateTimeOffset? LockoutEndsAt { get; private set; }

    public IReadOnlyCollection<RefreshToken> RefreshTokens => _refreshTokens.AsReadOnly();

    /// <summary>Creates an account with a temporary password that must be changed at first login.</summary>
    public static Result<User> Create(SchoolId? tenantId, Email email, string passwordHash, UserRole role, string firstName, string lastName, PhoneNumber? phone)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);

        // Invariant (appendix B 4.2): the Platform Admin has no school, every other role has one.
        if ((role == UserRole.PlatformAdmin) != (tenantId is null))
        {
            throw new ArgumentException("Only the Platform Admin has no school.", nameof(tenantId));
        }

        var first = firstName.Trim();
        var last = lastName.Trim();
        if (first.Length == 0 || last.Length == 0)
        {
            return Result.Failure<User>(CommonErrors.FieldRequired);
        }

        if (first.Length > NameMaxLength || last.Length > NameMaxLength)
        {
            return Result.Failure<User>(CommonErrors.FieldTooLong);
        }

        return Result.Success(new User(EntityId.New<UserId>(), tenantId, email, passwordHash, role, first, last, phone));
    }

    public Result CheckNotLockedOut(DateTimeOffset now) =>
        LockoutEndsAt is { } end && now < end ? Result.Failure(AuthErrors.AccountLocked) : Result.Success();

    /// <summary>
    /// Counts a wrong password. The fifth failure within 15 minutes of the first locks the account for
    /// 15 minutes and starts a fresh count (DEC-010). Returns the error the caller should report.
    /// </summary>
    public Error RecordFailedLogin(DateTimeOffset now)
    {
        if (CheckNotLockedOut(now).IsFailure)
        {
            return AuthErrors.AccountLocked;
        }

        if (FirstFailedLoginAt is not { } windowStart || now - windowStart >= FailedLoginWindow)
        {
            FirstFailedLoginAt = now;
            FailedLoginCount = 0;
        }

        FailedLoginCount++;
        if (FailedLoginCount < MaxFailedLogins)
        {
            return AuthErrors.InvalidCredentials;
        }

        LockoutEndsAt = now + LockoutDuration;
        ClearFailedLogins();
        return AuthErrors.AccountLocked;
    }

    /// <summary>The password was verified: clears failures and starts a new session (token family).</summary>
    public Result<RefreshToken> CompleteLogin(string refreshTokenHash, DateTimeOffset now)
    {
        if (Status != UserStatus.Active)
        {
            return Result.Failure<RefreshToken>(AuthErrors.AccountInactive);
        }

        ClearFailedLogins();
        LockoutEndsAt = null;
        return Result.Success(StartSession(refreshTokenHash, now));
    }

    /// <summary>
    /// Exchanges an active refresh token for a new one in the same family (rotation). Presenting a token
    /// that was already rotated is treated as theft: every token of the user is revoked (US-04).
    /// </summary>
    public Result<RefreshToken> RotateRefreshToken(string presentedHash, string newHash, DateTimeOffset now)
    {
        var current = FindToken(presentedHash);
        if (current is null || Status != UserStatus.Active)
        {
            return Result.Failure<RefreshToken>(AuthErrors.SessionExpired);
        }

        if (current.ReplacedByTokenId is not null)
        {
            RevokeAllRefreshTokens(now);
            return Result.Failure<RefreshToken>(AuthErrors.SessionExpired);
        }

        if (!current.IsActive(now))
        {
            return Result.Failure<RefreshToken>(AuthErrors.SessionExpired);
        }

        var successor = RefreshToken.Issue(Id, newHash, current.FamilyId, now, current.FamilyExpiresAt);
        current.ReplaceWith(successor, now);
        _refreshTokens.Add(successor);
        return Result.Success(successor);
    }

    /// <summary>Logout: revokes the presented token only. Unknown or already revoked tokens are ignored.</summary>
    public void RevokeRefreshToken(string tokenHash, DateTimeOffset now) => FindToken(tokenHash)?.Revoke(now);

    /// <summary>Sets the user's own password and ends every existing session (US-05).</summary>
    public void ChangePassword(string newPasswordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        PasswordHash = newPasswordHash;
        MustChangePassword = false;
        RevokeAllRefreshTokens(now);
        Raise(new PasswordChanged(Id));
    }

    /// <summary>Starts a session right after a password change, so the caller stays signed in.</summary>
    public RefreshToken StartSessionAfterPasswordChange(string refreshTokenHash, DateTimeOffset now) =>
        StartSession(refreshTokenHash, now);

    /// <summary>An admin gave the user a new temporary password: it must be changed, all sessions end, the lock is lifted.</summary>
    public void ResetPassword(string temporaryPasswordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPasswordHash);
        PasswordHash = temporaryPasswordHash;
        MustChangePassword = true;
        ClearFailedLogins();
        LockoutEndsAt = null;
        RevokeAllRefreshTokens(now);
    }

    /// <summary>Re-hashes the password with current parameters after a successful verification.</summary>
    public void UpgradePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public Result Deactivate(DateTimeOffset now)
    {
        if (Status == UserStatus.Deactivated)
        {
            return Result.Failure(UserErrors.AlreadyInactive);
        }

        Status = UserStatus.Deactivated;
        RevokeAllRefreshTokens(now);
        Raise(new UserDeactivated(Id, TenantId));
        return Result.Success();
    }

    private RefreshToken StartSession(string refreshTokenHash, DateTimeOffset now)
    {
        // Tokens of finished sessions can neither be used nor reused any more; drop them.
        _refreshTokens.RemoveAll(t => t.FamilyExpiresAt <= now);

        var token = RefreshToken.Issue(Id, refreshTokenHash, Guid.CreateVersion7(), now, now + SessionLifetime);
        _refreshTokens.Add(token);
        return token;
    }

    private void RevokeAllRefreshTokens(DateTimeOffset now)
    {
        foreach (var token in _refreshTokens)
        {
            token.Revoke(now);
        }
    }

    private RefreshToken? FindToken(string tokenHash) =>
        _refreshTokens.Find(t => string.Equals(t.TokenHash, tokenHash, StringComparison.Ordinal));

    private void ClearFailedLogins()
    {
        FailedLoginCount = 0;
        FirstFailedLoginAt = null;
    }
}
