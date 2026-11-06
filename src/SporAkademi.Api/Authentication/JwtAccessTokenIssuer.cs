using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Api.Authentication;

/// <summary>
/// Signs access tokens with the same <see cref="JwtOptions"/> the bearer handler validates (ADR-0006).
/// Claims are ids and flags only (DEC-052): no name, e-mail or phone.
/// </summary>
internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options) : IAccessTokenIssuer
{
    public const string SubjectClaim = "sub";
    public const string RoleClaim = "role";
    public const string PasswordChangeRequiredClaim = "pwd_change_required";

    private static readonly JsonWebTokenHandler Handler = new();

    public AccessToken Issue(User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        var jwt = options.Value;
        var expiresAt = now + jwt.AccessTokenLifetime;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SubjectClaim] = user.Id.Value.ToString(),
            [RoleClaim] = user.Role.ToString(),
            [PasswordChangeRequiredClaim] = user.MustChangePassword,
            [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
        };
        if (user.TenantId is { } tenant)
        {
            claims[HttpCurrentUser.TenantIdClaim] = tenant.Value.ToString();
        }

        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = JwtOptions.Issuer,
            Audience = JwtOptions.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.CreateSecurityKey(), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expiresAt);
    }
}
