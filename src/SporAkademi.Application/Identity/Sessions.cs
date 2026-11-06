using SporAkademi.Domain.Identity;

namespace SporAkademi.Application.Identity;

/// <summary>The signed-in user as the client sees it (login response and <c>GET /me</c>).</summary>
public sealed record UserSummary(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role,
    Guid? TenantId,
    bool MustChangePassword)
{
    public static UserSummary From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserSummary(user.Id.Value, user.Email.Value, user.FirstName, user.LastName, user.Role, user.TenantId?.Value, user.MustChangePassword);
    }
}

/// <summary>A started or renewed session: the access token for the body, the refresh token for the cookie.</summary>
public sealed record AuthSession(
    AccessToken AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserSummary User);

internal static class AuthSessions
{
    public static AuthSession Create(IAccessTokenIssuer issuer, User user, NewRefreshToken refreshToken, RefreshToken stored, DateTimeOffset now) =>
        new(issuer.Issue(user, now), refreshToken.Token, stored.ExpiresAt, UserSummary.From(user));
}
