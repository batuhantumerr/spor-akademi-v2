using System.Threading.RateLimiting;

namespace SporAkademi.Api.Authentication;

/// <summary>
/// NFR-SEC-14: login, refresh, change-password and password-reset attempts per client IP, each endpoint with its own
/// budget; the per-account limit is the lockout (DEC-010). Behind a reverse proxy the forwarded client
/// address must be applied first (Deploy block).
/// </summary>
internal static class AuthRateLimiting
{
    public const string Policy = "auth";
    public const int PermitsPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Password reset requests per client IP (DEC-353): they send e-mail, so the budget is small.</summary>
    public const string PasswordResetRequestPolicy = "password-reset-request";
    public const int PasswordResetRequestsPerWindow = 5;
    public static readonly TimeSpan PasswordResetRequestWindow = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"} {context.Request.Path.Value}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,
                    QueueLimit = 0,
                }));
            // The per-account limit is the cooldown in the domain (User.PasswordResetRequestCooldown).
            options.AddPolicy(PasswordResetRequestPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PasswordResetRequestsPerWindow,
                    Window = PasswordResetRequestWindow,
                    QueueLimit = 0,
                }));
        });
}
