using System.Threading.RateLimiting;

namespace SporAkademi.Api.Authentication;

/// <summary>
/// NFR-SEC-14: login attempts per client IP; the per-account limit is the lockout (DEC-010).
/// Behind a reverse proxy the forwarded client address must be applied first (Deploy block).
/// </summary>
internal static class LoginRateLimiting
{
    public const string Policy = "login";
    public const int PermitsPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddLoginRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,
                    QueueLimit = 0,
                }));
        });
}
