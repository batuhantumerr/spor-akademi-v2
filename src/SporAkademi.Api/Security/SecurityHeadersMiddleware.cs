namespace SporAkademi.Api.Security;

/// <summary>
/// NFR-SEC-13 (P4-01, DEC-500): security headers on every response of the API host. The API answers JSON (and the
/// SignalR hub), never a page, so its Content-Security-Policy allows nothing: a response opened in a browser tab can
/// run no script, load nothing, be framed nowhere. The Hangfire job panel (<c>/jobs</c>, DEC-332) is the one HTML
/// app on this host: its scripts are files of its own (no inline script), but its markup carries <c>style</c>
/// attributes, so its policy allows inline styles only. Set when the response starts, so problem responses written
/// by later middleware (and the exception handler, which clears headers) are covered too.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ApiPolicy = "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public const string JobsPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var jobs = context.Request.Path.StartsWithSegments("/jobs", StringComparison.OrdinalIgnoreCase);
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = jobs ? JobsPolicy : ApiPolicy;
            return Task.CompletedTask;
        });

        return next(context);
    }
}
