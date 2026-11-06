using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>Mints access tokens signed with the test key, for endpoints whose login flow is not under test.</summary>
public static class TestTokens
{
    public static string Create(Guid userId, string role, Guid? tenantId, DateTimeOffset now, bool passwordChangeRequired = false)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("role", role),
            new("jti", Guid.CreateVersion7().ToString()),
            new("pwd_change_required", passwordChangeRequired ? "true" : "false"),
        };
        if (tenantId is { } tenant)
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        var key = new SymmetricSecurityKey(Convert.FromHexString(ApiFactory.JwtSigningKey));
        var token = new JwtSecurityToken(
            issuer: "spor-akademi",
            audience: "spor-akademi-api",
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(15).UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static void Authorize(this HttpClient client, string token)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
