using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>
/// TestServer connections have no remote address. A host built with <see cref="Enable"/> takes the connection's
/// address from the <see cref="Header"/> request header, before any of the API's middleware runs, as if the request
/// had come from that address (a reverse proxy, or a client reaching the API directly).
/// </summary>
public static class RemoteAddress
{
    public const string Header = "X-Test-Remote-Address";

    public static void Enable(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, Filter>());
    }

    public static HttpRequestMessage From(this HttpRequestMessage request, string address, string? forwardedFor = null, string? forwardedProto = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Add(Header, address);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        return request;
    }

    private sealed class Filter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(Header, out var value) && IPAddress.TryParse(value, out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                    context.Request.Headers.Remove(Header);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
