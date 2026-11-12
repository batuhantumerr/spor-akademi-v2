using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace SporAkademi.Api.Errors;

/// <summary>
/// Gives every problem response the same shape: <c>code</c> and <c>traceId</c>, also for bare status
/// responses produced by the framework (401 without a token, 403, unknown routes). P3-10 (DEC-430): <c>title</c> and
/// <c>detail</c> are human-readable text in the language of <c>Accept-Language</c> (Turkish by default); clients keep
/// building their own messages from <c>code</c>, which never changes.
/// </summary>
internal static class ProblemShape
{
    private const string UnexpectedError = "Unexpected.Error";

    public static void Complete(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        var traceId = context.HttpContext.TraceIdentifier;
        problem.Instance = null;
        problem.Extensions["traceId"] = traceId;
        if (problem.Extensions.TryGetValue("code", out var existing) is false || existing is not string code)
        {
            code = problem.Status switch
            {
                StatusCodes.Status401Unauthorized => "Auth.Unauthenticated",
                StatusCodes.Status403Forbidden => "Auth.Forbidden",
                StatusCodes.Status404NotFound => "Resource.NotFound",
                StatusCodes.Status405MethodNotAllowed => "Request.MethodNotAllowed",
                StatusCodes.Status429TooManyRequests => "Request.TooManyRequests",
                _ => UnexpectedError,
            };
            problem.Extensions["code"] = code;
        }

        var language = ProblemLanguage.From(context.HttpContext);
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (problem.Extensions.TryGetValue("args", out var args) && args is IReadOnlyDictionary<string, object?> given)
        {
            foreach (var (key, value) in given)
            {
                arguments[key] = value;
            }
        }
        if (code == UnexpectedError)
        {
            arguments["traceId"] = traceId;
        }

        problem.Title = ProblemTexts.Title(problem.Status, language);
        problem.Detail = ProblemTexts.Detail(code, language, arguments);
        context.HttpContext.Response.Headers[HeaderNames.ContentLanguage] = language;
        context.HttpContext.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptLanguage);
    }
}
