using Microsoft.AspNetCore.Http;

namespace SporAkademi.Api.Errors;

/// <summary>
/// Gives every problem response the same shape: <c>code</c> and <c>traceId</c>, also for bare status
/// responses produced by the framework (401 without a token, 403, unknown routes).
/// </summary>
internal static class ProblemShape
{
    public static void Complete(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        problem.Instance = null;
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        if (!problem.Extensions.ContainsKey("code"))
        {
            var code = problem.Status switch
            {
                StatusCodes.Status401Unauthorized => "Auth.Unauthenticated",
                StatusCodes.Status403Forbidden => "Auth.Forbidden",
                StatusCodes.Status404NotFound => "Resource.NotFound",
                StatusCodes.Status405MethodNotAllowed => "Request.MethodNotAllowed",
                StatusCodes.Status429TooManyRequests => "Request.TooManyRequests",
                _ => "Unexpected.Error",
            };
            problem.Extensions["code"] = code;
            problem.Title = code;
        }
    }
}
