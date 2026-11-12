using Microsoft.AspNetCore.Mvc;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Errors;

/// <summary>Maps expected failures to RFC 9457 problem details with a stable code (ADR-0007, appendix E).</summary>
internal static class ProblemResults
{
    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IResult Problem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = StatusFor(error.Type);
        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"https://httpstatuses.io/{status}",
        };
        problem.Extensions["code"] = error.Code;

        var arguments = error.Arguments
            .Where(a => a.Key != ValidationErrors.ErrorsArgument)
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        if (arguments.Count > 0)
        {
            problem.Extensions["args"] = arguments;
        }

        if (error.Arguments.TryGetValue(ValidationErrors.ErrorsArgument, out var errors))
        {
            problem.Extensions["errors"] = errors;
        }

        // traceId and the localized title/detail (P3-10) are added by ProblemShape, the ProblemDetails customization.
        return TypedResults.Problem(problem);
    }

    public static IResult ToProblem(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsFailure
            ? Problem(result.Error)
            : throw new InvalidOperationException("A successful result is not a problem.");
    }
}
