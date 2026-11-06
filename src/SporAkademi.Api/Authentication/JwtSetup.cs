using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SporAkademi.Api.Authentication;

/// <summary>Access token settings (ADR-0006). The signing key is 32+ bytes of hex from configuration.</summary>
internal sealed class JwtOptions
{
    public const string Section = "Jwt";
    public const string Issuer = "spor-akademi";
    public const string Audience = "spor-akademi-api";

    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public SymmetricSecurityKey CreateSecurityKey() => new(Convert.FromHexString(SigningKey));

    public static bool IsValidKey(string key) =>
        key.Length >= 64 && key.Length % 2 == 0 && key.All(char.IsAsciiHexDigit);
}

internal static class JwtSetup
{
    /// <summary>Bearer authentication plus a fallback policy: every endpoint requires a user unless it opts out (NFR-SEC-07).</summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.Section)
            .Validate(o => JwtOptions.IsValidKey(o.SigningKey), "Jwt:SigningKey must be at least 32 bytes of hex.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwt, timeProvider) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = JwtOptions.Issuer,
                    ValidAudience = JwtOptions.Audience,
                    IssuerSigningKey = JwtOptions.IsValidKey(jwt.Value.SigningKey) ? jwt.Value.CreateSecurityKey() : null,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // Token lifetimes follow TimeProvider like every other time rule, so tests can move the clock.
                    LifetimeValidator = (notBefore, expires, _, parameters) =>
                    {
                        var now = timeProvider.GetUtcNow().UtcDateTime;
                        return expires is { } end
                            && end > now - parameters.ClockSkew
                            && (notBefore is not { } start || start <= now + parameters.ClockSkew);
                    },
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

}
