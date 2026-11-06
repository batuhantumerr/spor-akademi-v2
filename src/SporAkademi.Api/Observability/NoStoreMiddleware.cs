namespace SporAkademi.Api.Observability;

/// <summary>
/// API responses carry tokens, temporary passwords and personal data: no browser or proxy may store them.
/// Set when the response starts, so problem responses from later middleware are covered too.
/// </summary>
internal sealed class NoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
