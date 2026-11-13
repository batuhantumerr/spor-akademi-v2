using SporAkademi.Application.Abstractions.Auditing;

namespace SporAkademi.Api.Observability;

/// <summary>The request's correlation id (the trace identifier set by <see cref="CorrelationIdMiddleware"/>), for the audit log.</summary>
internal sealed class HttpRequestCorrelation(IHttpContextAccessor accessor) : IRequestCorrelation
{
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;
}
