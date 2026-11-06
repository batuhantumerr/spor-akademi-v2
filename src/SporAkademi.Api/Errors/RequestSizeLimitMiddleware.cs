using Microsoft.AspNetCore.Http.Features;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Errors;

/// <summary>
/// NFR-SEC-12: bodies over 1 MB are rejected with <c>Request.TooLarge</c>. Declared lengths are checked
/// here (works on every server); streamed bodies are capped by the server feature where supported.
/// </summary>
internal sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public const long MaxBodyBytes = 1_048_576;

    private static readonly Error TooLarge = Error.Validation("Request.TooLarge", "The request body is too large.");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            await ProblemResults.Problem(TooLarge).ExecuteAsync(context);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = MaxBodyBytes;
        }

        await next(context);
    }
}
