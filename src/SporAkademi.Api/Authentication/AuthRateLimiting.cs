using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using SporAkademi.Api.Security;

namespace SporAkademi.Api.Authentication;

/// <summary>
/// NFR-SEC-14: login, refresh, change-password and password-reset attempts per client IP, each endpoint with its own
/// budget; the per-account limit is the lockout (DEC-010). Behind a reverse proxy the forwarded client
/// address must be applied first (Deploy block). The budget is <c>RateLimiting:AuthPermitsPerMinute</c> (10 by
/// default); only a load test raises it (P4-08, DEC-561).
/// </summary>
internal static class AuthRateLimiting
{
    public const string Policy = "auth";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Password reset requests per client IP (DEC-353): they send e-mail, so the budget is small.</summary>
    public const string PasswordResetRequestPolicy = "password-reset-request";
    public const int PasswordResetRequestsPerWindow = 5;
    public static readonly TimeSpan PasswordResetRequestWindow = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                // The client as the general limit sees it (DEC-569): IPv4-mapped written as IPv4, IPv6 per /64.
                $"{ApiRateLimiting.ClientNetwork(context.Connection.RemoteIpAddress)} {context.Request.Path.Value}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value.AuthPermitsPerMinute,
                    Window = Window,
                    QueueLimit = 0,
                }));
            // The per-account limit is the cooldown in the domain (User.PasswordResetRequestCooldown).
            options.AddPolicy(PasswordResetRequestPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ApiRateLimiting.ClientNetwork(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PasswordResetRequestsPerWindow,
                    Window = PasswordResetRequestWindow,
                    QueueLimit = 0,
                }));
        });
}
