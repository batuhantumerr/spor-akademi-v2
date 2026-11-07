namespace SporAkademi.Api.Observability;

/// <summary>
/// API responses carry tokens, temporary passwords and personal data: no browser or proxy may store them. The job
/// panel (<c>/jobs</c>, DEC-332) is covered too and may not be framed (its actions are buttons).
/// Set when the response starts, so problem responses from later middleware are covered too.
/// </summary>
internal sealed class NoStoreMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var api = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        var jobs = context.Request.Path.StartsWithSegments("/jobs", StringComparison.OrdinalIgnoreCase);
        if (api || jobs)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (jobs)
                {
                    context.Response.Headers.XFrameOptions = "DENY";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                }

                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
