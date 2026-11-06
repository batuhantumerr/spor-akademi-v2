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
                bearer.Events = new JwtBearerEvents { OnTokenValidated = RejectTokensWithoutValidRoleAndTenant };
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


    /// <summary>A school role without a well-formed tenant id is never a valid token (ADR-0003).</summary>
    private static Task RejectTokensWithoutValidRoleAndTenant(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var validRole = Enum.TryParse<SporAkademi.Application.Abstractions.Authentication.Role>(principal?.FindFirst("role")?.Value, out var role);
        var hasTenant = Guid.TryParse(principal?.FindFirst(HttpCurrentUser.TenantIdClaim)?.Value, out _);
        var valid = validRole && (role == SporAkademi.Application.Abstractions.Authentication.Role.PlatformAdmin ? !hasTenant : hasTenant)
            && Guid.TryParse(principal?.FindFirst("sub")?.Value, out _);
        if (!valid)
        {
            context.Fail("Token claims are inconsistent.");
        }

        return Task.CompletedTask;
    }
}
