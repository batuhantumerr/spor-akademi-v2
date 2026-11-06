using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity;

public enum PasswordVerification
{
    Failed,
    Success,

    /// <summary>Correct, but hashed with outdated parameters; the caller re-hashes it.</summary>
    SuccessRehashNeeded,
}

/// <summary>Salted, slow password hashing (NFR-SEC-03). Implemented with ASP.NET Core's <c>PasswordHasher</c>; no own crypto.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Verify(string passwordHash, string providedPassword);

    /// <summary>Spends the time of one verification, so an unknown e-mail answers as slowly as a wrong password.</summary>
    void SpendVerificationTime(string providedPassword);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Issues the short-lived access token (ADR-0006): claims carry ids and flags only, no personal data.</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(User user, DateTimeOffset now);
}

/// <summary>A new opaque refresh token: <see cref="Token"/> goes to the client once, only <see cref="Hash"/> is stored.</summary>
public sealed record NewRefreshToken(string Token, string Hash);

public interface IRefreshTokenFactory
{
    /// <summary>256 random bits, URL-safe text, and its SHA-256 hash.</summary>
    NewRefreshToken Create();

    string Hash(string token);
}

/// <summary>Loads users with their refresh tokens. Users are not tenant-filtered (DEC-105).</summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken);

    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<User?> GetByRefreshTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<UserSummary?> GetSummaryAsync(UserId id, CancellationToken cancellationToken);
}
