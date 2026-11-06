using System.Threading.RateLimiting;

namespace SporAkademi.Api.Authentication;

/// <summary>
/// NFR-SEC-14: login, refresh and change-password attempts per client IP, each endpoint with its own
/// budget; the per-account limit is the lockout (DEC-010). Behind a reverse proxy the forwarded client
/// address must be applied first (Deploy block).
/// </summary>
internal static class AuthRateLimiting
{
    public const string Policy = "auth";
    public const int PermitsPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"} {context.Request.Path.Value}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,
                    QueueLimit = 0,
                }));
        });
}
