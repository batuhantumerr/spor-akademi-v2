using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Errors;

/// <summary>
/// NFR-SEC-12: bodies over 1 MB are rejected with <c>Request.TooLarge</c>. Declared lengths are checked
/// here (works on every server); streamed bodies are capped by the server feature where supported. An endpoint
/// may declare a different limit with <see cref="IRequestSizeLimitMetadata"/> (only the document upload does, DEC-321).
/// </summary>
internal sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public const long MaxBodyBytes = 1_048_576;

    private static readonly Error TooLarge = Error.Validation("Request.TooLarge", "The request body is too large.");

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var limit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? MaxBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            await ProblemResults.Problem(TooLarge).ExecuteAsync(context);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        await next(context);
    }
}
