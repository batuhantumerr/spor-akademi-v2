using Serilog.Context;
using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Api.Observability;

/// <summary>
/// Every request gets a correlation id (from <c>X-Correlation-Id</c> when well-formed, otherwise new).
/// It becomes the trace identifier, is echoed in the response and enriches every log event,
/// together with the tenant and user ids once authenticated (NFR-OBS-01, NFR-OBS-02).
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string CorrelationTag = "spor_akademi.correlation_id";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsWellFormed(incoming) ? incoming : Guid.CreateVersion7().ToString("N");

        context.TraceIdentifier = correlationId;
        // P4-07 (DEC-553): the request's span carries the correlation id, and log events carry the span's TraceId
        // (Serilog takes it from the current activity), so a problem response's traceId leads to its trace and logs.
        System.Diagnostics.Activity.Current?.SetTag(CorrelationTag, correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    private static bool IsWellFormed(string value) =>
        value.Length is > 0 and <= MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

/// <summary>Adds tenant and user ids to log events once authentication has run (ids only, no personal data).</summary>
internal sealed class UserLogEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser)
    {
        using (LogContext.PushProperty("TenantId", currentUser.TenantId))
        using (LogContext.PushProperty("UserId", currentUser.UserId))
        {
            await next(context);
        }
    }
}
