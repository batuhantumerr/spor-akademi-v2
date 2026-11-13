using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace SporAkademi.Api.Security;

/// <summary>The general API limits (<c>RateLimiting</c> section, DEC-504); requests per minute.</summary>
internal sealed class RateLimitingOptions
{
    public const string Section = "RateLimiting";

    /// <summary>All <c>/api</c> requests of one signed-in user (the token's subject).</summary>
    [Range(1, int.MaxValue)]
    public int UserPermitsPerMinute { get; set; } = 300;

    /// <summary>POST, PUT, PATCH and DELETE requests of all users of one school together.</summary>
    [Range(1, int.MaxValue)]
    public int SchoolWritePermitsPerMinute { get; set; } = 300;

    /// <summary>All <c>/api</c> requests without a valid token from one client IP.</summary>
    [Range(1, int.MaxValue)]
    public int AnonymousPermitsPerMinute { get; set; } = 120;

    /// <summary>
    /// Sign-in, refresh and password change attempts per client IP and endpoint (NFR-SEC-14). Raised only for a load test,
    /// which signs many accounts in from one address (P4-08, DEC-561); the production value is the default.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int AuthPermitsPerMinute { get; set; } = 10;

    /// <summary>Requests per parent account over all parent endpoints (DEC-388); like the sign-in limit, raised only for a load test.</summary>
    [Range(1, int.MaxValue)]
    public int ParentPermitsPerMinute { get; set; } = 60;

    /// <summary>True when no limit is above its production value (the defaults); lower values are always allowed.</summary>
    public bool WithinProductionValues()
    {
        var production = new RateLimitingOptions();
        return UserPermitsPerMinute <= production.UserPermitsPerMinute
            && SchoolWritePermitsPerMinute <= production.SchoolWritePermitsPerMinute
            && AnonymousPermitsPerMinute <= production.AnonymousPermitsPerMinute
            && AuthPermitsPerMinute <= production.AuthPermitsPerMinute
            && ParentPermitsPerMinute <= production.ParentPermitsPerMinute;
    }
}

/// <summary>
/// NFR-SEC-14 (P4-02, DEC-503 – DEC-506): a general limit in front of the per-endpoint ones (sign-in, refresh, password
/// reset, invitations, exports, the parent API, the hub's negotiation). Every <c>/api</c> request counts towards its
/// caller's budget, the signed-in user or else the client IP; writes also count towards the school's write budget.
/// Payment webhooks have their own, larger budget (DEC-344) and are left out, as are health checks, the hub's
/// transport and the job panel (not under <c>/api</c>). Fixed one-minute windows in memory: each API instance counts on
/// its own (DEC-506). Rejections are <c>429</c> problems with the code <c>Request.TooManyRequests</c> (written by the
/// status code pages, ProblemShape) and <c>Retry-After</c> in seconds.
/// </summary>
internal static class ApiRateLimiting
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.Section)
            .ValidateDataAnnotations()
            // DEC-561: a limit may be lowered anywhere, but raised only outside Production (the load test environment);
            // in Production a raised value stops the start instead of silently weakening NFR-SEC-14.
            .Validate<IHostEnvironment>(
                (options, environment) => !environment.IsProduction() || options.WithinProductionValues(),
                "A RateLimiting value is above the production value (DEC-504, DEC-561); only a load test environment may raise it.")
            .ValidateOnStart();

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
                var seconds = Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, int.MaxValue);
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            // The chain takes the caller's permit first; when the school's write budget then refuses, that permit is not
            // given back (fixed windows cannot return one), and Retry-After is mostly the rest of the school's window
            // (DEC-503).
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(Caller),
                PartitionedRateLimiter.Create<HttpContext, string>(SchoolWrites));
        });
    }

    private static RateLimitPartition<string> Caller(HttpContext context)
    {
        if (!IsLimited(context))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        var settings = Settings(context);
        return context.User.FindFirst("sub")?.Value is { Length: > 0 } user
            ? FixedWindow($"user:{user}", settings.UserPermitsPerMinute)
            : FixedWindow($"ip:{ClientNetwork(context.Connection.RemoteIpAddress)}", settings.AnonymousPermitsPerMinute);
    }

    /// <summary>
    /// The anonymous bucket: an IPv4 address, or the /64 network of an IPv6 address, since one subscriber usually gets a
    /// whole /64 and could otherwise take a fresh address, and a fresh bucket, for every request (DEC-504).
    /// </summary>
    internal static string ClientNetwork(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static RateLimitPartition<string> SchoolWrites(HttpContext context)
    {
        var method = context.Request.Method;
        var write = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
        return write && IsLimited(context) && context.User.FindFirst("tenant_id")?.Value is { Length: > 0 } school
            ? FixedWindow($"school:{school}", Settings(context).SchoolWritePermitsPerMinute)
            : RateLimitPartition.GetNoLimiter(string.Empty);
    }

    private static bool IsLimited(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/api/v1/payment-webhooks", StringComparison.OrdinalIgnoreCase);

    private static RateLimitingOptions Settings(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

    private static RateLimitPartition<string> FixedWindow(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = Window,
            QueueLimit = 0,
        });
}
