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

    /// <summary>A guardian of students of one school (P3-01, DEC-377); such an account is opened only by an invitation.</summary>
    Parent,
}

public enum UserStatus
{
    Active,
    Deactivated,
}

public sealed record PasswordChanged(UserId UserId) : IDomainEvent;

public sealed record UserDeactivated(UserId UserId, SchoolId? TenantId) : IDomainEvent;

/// <summary>A new account was opened (US-01, US-08); P2-F4 sends the welcome e-mail after commit.</summary>
public sealed record UserCreated(UserId UserId, SchoolId? TenantId) : IDomainEvent;

/// <summary>A parent account was invited or invited again (P3-02, DEC-378); the invitation e-mail goes out after commit.</summary>
public sealed record ParentInvited(UserId UserId, SchoolId TenantId) : IDomainEvent;

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

    /// <summary>DEC-353: one self-service reset request per account in this window; later requests are ignored silently.</summary>
    public static readonly TimeSpan PasswordResetRequestCooldown = TimeSpan.FromMinutes(2);

    private readonly List<RefreshToken> _refreshTokens = [];
    private readonly List<PasswordToken> _passwordTokens = [];

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

    /// <summary>The last accepted self-service reset request (DEC-353 cooldown).</summary>
    public DateTimeOffset? PasswordResetRequestedAt { get; private set; }

    public IReadOnlyCollection<RefreshToken> RefreshTokens => _refreshTokens.AsReadOnly();

    /// <summary>The pending set-password link, if any (at most one, DEC-352).</summary>
    public IReadOnlyCollection<PasswordToken> PasswordTokens => _passwordTokens.AsReadOnly();

    /// <summary>Creates an account with a temporary password that must be changed at first login.</summary>
    public static Result<User> Create(SchoolId? tenantId, Email email, string passwordHash, UserRole role, string firstName, string lastName, PhoneNumber? phone)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        // Invariant (appendix B 4.2): the Platform Admin has no school, every other role has one.
        if ((role == UserRole.PlatformAdmin) != (tenantId is null))
        {
            throw new ArgumentException("Only the Platform Admin has no school.", nameof(tenantId));
        }

        if (role == UserRole.Parent)
        {
            throw new ArgumentException("A parent account is opened by an invitation (InviteParent).", nameof(role));
        }

        var names = CheckNames(firstName, lastName);
        if (names.IsFailure)
        {
            return Result.Failure<User>(names.Error);
        }

        var user = new User(EntityId.New<UserId>(), tenantId, email, passwordHash, role, names.Value.First, names.Value.Last, phone);
        user.Raise(new UserCreated(user.Id, tenantId));
        return Result.Success(user);
    }

    /// <summary>
    /// Opens a parent account for a guardian (P3-02, DEC-378). It has no usable password: <paramref name="unusablePasswordHash"/>
    /// hashes a random secret nobody knows, so the only way in is the invitation link, which sets the password. Raises
    /// <see cref="ParentInvited"/> instead of <see cref="UserCreated"/>, so no admin-panel welcome e-mail is sent.
    /// </summary>
    public static Result<User> InviteParent(SchoolId tenantId, Email email, string unusablePasswordHash, string firstName, string lastName, PhoneNumber? phone)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(unusablePasswordHash);
        var names = CheckNames(firstName, lastName);
        if (names.IsFailure)
        {
            return Result.Failure<User>(names.Error);
        }

        var user = new User(EntityId.New<UserId>(), tenantId, email, unusablePasswordHash, UserRole.Parent, names.Value.First, names.Value.Last, phone);
        user.Raise(new ParentInvited(user.Id, tenantId));
        return Result.Success(user);
    }

    /// <summary>
    /// Invites the parent again (P3-02, DEC-378): a pending invitation is resent, a closed account is reopened. Every
    /// earlier link and session ends at once, the account is back to "invited" with a new unusable password, and the
    /// login becomes <paramref name="email"/> (the guardian's current address). An account whose parent already set a
    /// password is not invited again: <c>ParentAccount.AlreadyActive</c>.
    /// </summary>
    public Result ReinviteParent(Email email, string unusablePasswordHash, string firstName, string lastName, PhoneNumber? phone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(unusablePasswordHash);
        if (Role != UserRole.Parent || TenantId is not { } tenant)
        {
            throw new InvalidOperationException("Only a parent account can be invited again.");
        }

        if (Status == UserStatus.Active && !MustChangePassword)
        {
            return Result.Failure(ParentAccountErrors.AlreadyActive);
        }

        var names = CheckNames(firstName, lastName);
        if (names.IsFailure)
        {
            return Result.Failure(names.Error);
        }

        (FirstName, LastName, Phone, Email) = (names.Value.First, names.Value.Last, phone, email);
        Status = UserStatus.Active;
        PasswordHash = unusablePasswordHash;
        MustChangePassword = true;
        ClearFailedLogins();
        LockoutEndsAt = null;
        PasswordResetRequestedAt = null;
        RevokeAllRefreshTokens(now);
        _passwordTokens.Clear();
        Raise(new ParentInvited(Id, tenant));
        return Result.Success();
    }

    /// <summary>
    /// DEC-379: the guardian this parent account belonged to was deleted (DEC-365, DEC-368), so the account's personal
    /// data goes as well: it is closed (sessions and links end) and its name, phone and e-mail are replaced by
    /// placeholders. The address is freed for a new account; the row stays only so ids in logs and records keep meaning.
    /// </summary>
    public void EraseParent(DateTimeOffset now)
    {
        if (Role != UserRole.Parent)
        {
            throw new InvalidOperationException("Only a parent account is erased with its guardian.");
        }

        if (Status == UserStatus.Active)
        {
            _ = Deactivate(now);
        }

        RevokeAllRefreshTokens(now);
        _passwordTokens.Clear();
        (FirstName, LastName, Phone) = (ErasedParentFirstName, ErasedParentLastName, null);
        Email = Email.Create("silinmis-veli-" + Id.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture) + "@silinmis.invalid").Value;
    }

    /// <summary>The name an erased parent account gets (DEC-379).</summary>
    public const string ErasedParentFirstName = "Silinmiş";

    public const string ErasedParentLastName = "Veli";

    /// <summary>When the pending invitation link expires, if the newest link of the account is an invitation (DEC-378).</summary>
    public DateTimeOffset? InvitationExpiresAt =>
        _passwordTokens.Find(t => t.Purpose == PasswordTokenPurpose.Invitation)?.ExpiresAt;

    /// <summary>Changes the editable profile (US-08: name and phone) of an active account. The e-mail is the login and does not change here.</summary>
    public Result UpdateProfile(string firstName, string lastName, PhoneNumber? phone)
    {
        // DEC-126: a deactivated account is read-only.
        if (Status == UserStatus.Deactivated)
        {
            return Result.Failure(UserErrors.AlreadyInactive);
        }

        var names = CheckNames(firstName, lastName);
        if (names.IsFailure)
        {
            return Result.Failure(names.Error);
        }

        FirstName = names.Value.First;
        LastName = names.Value.Last;
        Phone = phone;
        return Result.Success();
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

    /// <summary>
    /// Sets the user's own password (US-05): the temporary-password flag clears, every existing session
    /// ends and a new session starts for the caller, so they stay signed in on this device only.
    /// </summary>
    public Result<RefreshToken> ChangePassword(string newPasswordHash, string refreshTokenHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        if (Status != UserStatus.Active)
        {
            return Result.Failure<RefreshToken>(AuthErrors.AccountInactive);
        }

        PasswordHash = newPasswordHash;
        MustChangePassword = false;
        ClearFailedLogins();
        RevokeAllRefreshTokens(now);
        _passwordTokens.Clear();
        Raise(new PasswordChanged(Id));
        return Result.Success(StartSession(refreshTokenHash, now));
    }

    /// <summary>
    /// Accepts a self-service reset request unless one was accepted within <see cref="PasswordResetRequestCooldown"/>
    /// (DEC-353). Only active accounts get a link; the caller answers the same either way.
    /// </summary>
    public bool TryRequestPasswordReset(DateTimeOffset now)
    {
        if (Status != UserStatus.Active
            || (PasswordResetRequestedAt is { } last && now - last < PasswordResetRequestCooldown))
        {
            return false;
        }

        PasswordResetRequestedAt = now;
        return true;
    }

    /// <summary>
    /// Issues a set-password link (DEC-352): it replaces any earlier one, so only the newest link works. Only its
    /// hash is kept.
    /// </summary>
    public Result<PasswordToken> IssuePasswordToken(string tokenHash, PasswordTokenPurpose purpose, DateTimeOffset now)
    {
        if (Status != UserStatus.Active)
        {
            return Result.Failure<PasswordToken>(AuthErrors.AccountInactive);
        }

        _passwordTokens.Clear();
        var token = PasswordToken.Issue(Id, tokenHash, purpose, now);
        _passwordTokens.Add(token);
        return Result.Success(token);
    }

    /// <summary>
    /// Sets a new password with a set-password link (P2-F4). The link is used up, the temporary-password flag and
    /// any lockout clear, and every session ends: the user signs in again with the new password. An unknown, expired
    /// or already used link, and any link of a deactivated account, is <c>Auth.ResetTokenInvalid</c>.
    /// </summary>
    public Result SetPasswordWithToken(string tokenHash, string newPasswordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        var token = _passwordTokens.Find(t => string.Equals(t.TokenHash, tokenHash, StringComparison.Ordinal));
        if (token is null || !token.IsValid(now) || Status != UserStatus.Active)
        {
            return Result.Failure(AuthErrors.ResetTokenInvalid);
        }

        PasswordHash = newPasswordHash;
        MustChangePassword = false;
        ClearFailedLogins();
        LockoutEndsAt = null;
        RevokeAllRefreshTokens(now);
        _passwordTokens.Clear();
        Raise(new PasswordChanged(Id));
        return Result.Success();
    }

    /// <summary>An admin gave the user a new temporary password: it must be changed, all sessions end, the lock is lifted.</summary>
    public void ResetPassword(string temporaryPasswordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPasswordHash);
        PasswordHash = temporaryPasswordHash;
        MustChangePassword = true;
        ClearFailedLogins();
        LockoutEndsAt = null;
        RevokeAllRefreshTokens(now);
        _passwordTokens.Clear();
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
        _passwordTokens.Clear();
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

    private static Result<(string First, string Last)> CheckNames(string firstName, string lastName)
    {
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);

        var first = firstName.Trim();
        var last = lastName.Trim();
        if (first.Length == 0 || last.Length == 0)
        {
            return Result.Failure<(string, string)>(CommonErrors.FieldRequired);
        }

        return first.Length > NameMaxLength || last.Length > NameMaxLength
            ? Result.Failure<(string, string)>(CommonErrors.FieldTooLong)
            : Result.Success((first, last));
    }

    private void ClearFailedLogins()
    {
        FailedLoginCount = 0;
        FirstFailedLoginAt = null;
    }
}
