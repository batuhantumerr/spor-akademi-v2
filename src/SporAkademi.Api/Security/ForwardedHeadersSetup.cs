using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SporAkademi.Api.Security;

/// <summary>
/// P4-02 (DEC-507): behind the TLS-terminating reverse proxy (Caddy, Deploy block) the connection comes from the proxy,
/// so the client address (rate limits, DEC-504) and the scheme (HSTS, DEC-501) come from <c>X-Forwarded-For</c> and
/// <c>X-Forwarded-Proto</c>. Only from the proxies named in <c>ForwardedHeaders:KnownProxies</c> (addresses) or
/// <c>ForwardedHeaders:KnownNetworks</c> (CIDR): by default the list is empty and the headers are ignored, so a client
/// cannot pick its own address. One proxy hop (<c>ForwardLimit</c> 1). An invalid entry stops the start.
/// </summary>
internal static partial class ForwardedHeadersSetup
{
    public const string Section = "ForwardedHeaders";

    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // The framework trusts loopback by default; nothing is trusted unless configured.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005 // the obsolete list still holds the loopback network by default and is still checked
            options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
            foreach (var proxy in Entries(configuration, "KnownProxies"))
            {
                options.KnownProxies.Add(IPAddress.TryParse(proxy, out var address)
                    ? address
                    : throw new InvalidOperationException($"{Section}:KnownProxies has an invalid address: {proxy}"));
            }

            foreach (var network in Entries(configuration, "KnownNetworks"))
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(network, out var parsed)
                    ? parsed
                    : throw new InvalidOperationException($"{Section}:KnownNetworks has an invalid CIDR network: {network}"));
            }
        });
        return services;
    }

    /// <summary>
    /// Applies the forwarded headers when a proxy is configured. The framework's middleware trusts <em>every</em> source
    /// when both lists are empty, so without a configured proxy it is not added at all; in Production that is said
    /// once at start (the API is then assumed to be reached directly).
    /// </summary>
    public static WebApplication UseTrustedForwardedHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions>>().Value;
        if (options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0)
        {
            app.UseForwardedHeaders();
        }
        else if (app.Environment.IsProduction())
        {
            LogNoTrustedProxy(app.Logger);
        }

        return app;
    }

    private static IEnumerable<string> Entries(IConfiguration configuration, string key) =>
        configuration.GetSection($"{Section}:{key}").GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "No trusted reverse proxy is configured (ForwardedHeaders:KnownProxies/KnownNetworks): X-Forwarded-For and "
        + "X-Forwarded-Proto are ignored. Behind a proxy every anonymous client shares the proxy's rate limit and HSTS is never sent.")]
    private static partial void LogNoTrustedProxy(ILogger logger);
}
