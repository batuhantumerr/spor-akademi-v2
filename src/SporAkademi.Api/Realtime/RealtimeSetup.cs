using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Realtime;
using StackExchange.Redis;

namespace SporAkademi.Api.Realtime;

/// <summary>
/// P3-08: SignalR with the hub at <see cref="NotificationHub.Path"/> (DEC-396 – DEC-401). JSON protocol only, nothing to
/// receive from clients beyond the handshake and pings (4 KB), no detailed errors. With <c>ConnectionStrings:Cache</c>
/// the Valkey backplane carries messages between API instances; it is decided when the hub is first used, like the
/// cache (DEC-315), so hosts and tests supply the connection string through any configuration source.
/// </summary>
internal static class RealtimeSetup
{
    /// <summary>DEC-400: negotiations per user (or client IP) and minute; the transport requests themselves are not limited.</summary>
    public const string RateLimitPolicy = "realtime";
    public const int NegotiationsPerMinute = 30;

    /// <summary>Messages of this application on a shared Valkey are kept apart from any other user of it.</summary>
    public const string BackplaneChannelPrefix = "spor-akademi:signalr";

    public static IServiceCollection AddRealtime(this IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 4 * 1024;
            options.MaximumParallelInvocationsPerClient = 1;
        });

        services.AddOptions<RedisOptions>().Configure<IConfiguration>((redis, configuration) =>
        {
            var connectionString = configuration.GetConnectionString("Cache");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            redis.Configuration = ConfigurationOptions.Parse(connectionString);
            redis.Configuration.ChannelPrefix = RedisChannel.Literal(BackplaneChannelPrefix);
            // Keep retrying in the background after an outage instead of failing every later send.
            redis.Configuration.AbortOnConnectFail = false;
        });
        services.AddSingleton<HubLifetimeManager<NotificationHub>>(provider =>
            string.IsNullOrWhiteSpace(provider.GetRequiredService<IConfiguration>().GetConnectionString("Cache"))
                ? ActivatorUtilities.CreateInstance<DefaultHubLifetimeManager<NotificationHub>>(provider)
                : ActivatorUtilities.CreateInstance<RedisHubLifetimeManager<NotificationHub>>(provider, provider.GetRequiredService<IOptions<RedisOptions>>()));

        // Registered before AddInfrastructure, which only adds a do-nothing fallback.
        services.AddSingleton<IRealtimeNotifier, HubRealtimeNotifier>();

        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
            context.Request.Path.Value?.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) == true
                ? RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = NegotiationsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("transport")));

        return services;
    }

    public static IEndpointRouteBuilder MapRealtime(this IEndpointRouteBuilder app)
    {
        app.MapHub<NotificationHub>(NotificationHub.Path, options =>
            {
                // A connection lives only as long as its access token (15 minutes); the client reconnects with a fresh one,
                // so a signed-out or deactivated user stops receiving at the latest then (DEC-399).
                options.CloseOnAuthenticationExpiration = true;
                options.ApplicationMaxBufferSize = 4 * 1024;
            })
            .RequireRateLimiting(RateLimitPolicy);
        return app;
    }

    /// <summary>
    /// Browsers cannot send headers on a WebSocket, so SignalR passes the token as <c>access_token</c> in the query. It is
    /// accepted only on the hub path and only when no Authorization header is present (DEC-399); logs redact it.
    /// </summary>
    public static Task ReadHubTokenFromQuery(Microsoft.AspNetCore.Authentication.JwtBearer.MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.HttpContext.Request;
        if (request.Path.StartsWithSegments(NotificationHub.Path, StringComparison.OrdinalIgnoreCase)
            && request.Headers.Authorization.Count == 0
            && request.Query.TryGetValue("access_token", out var token)
            && token.Count == 1
            && !string.IsNullOrEmpty(token[0]))
        {
            context.Token = token[0];
        }

        return Task.CompletedTask;
    }
}
