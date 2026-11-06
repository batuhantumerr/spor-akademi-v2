using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

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

    Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken);

    void Add(User user);

    /// <summary>Optimistic concurrency: the save fails with <c>Concurrency.Conflict</c> if the row changed since <paramref name="version"/>.</summary>
    void ExpectVersion(User user, uint version);

    /// <summary>A coach of <paramref name="schoolId"/> with its tokens; other roles and schools are not found (DEC-105: explicit tenant).</summary>
    Task<User?> GetCoachAsync(SchoolId schoolId, UserId id, CancellationToken cancellationToken);

    Task<CoachDto?> FindCoachDtoAsync(SchoolId schoolId, UserId id, CancellationToken cancellationToken);

    /// <summary>Coaches of the school by last name, then first name.</summary>
    Task<PagedResult<CoachDto>> ListCoachesAsync(SchoolId schoolId, PageRequest page, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<SchoolId, SchoolAdminSummary>> GetFirstSchoolAdminsAsync(IReadOnlyCollection<SchoolId> schoolIds, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> GetActiveCoachIdsAsync(SchoolId schoolId, IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken);
}

public sealed record CoachDto(Guid Id, string FirstName, string LastName, string Email, string? Phone, UserStatus Status, uint Version);
