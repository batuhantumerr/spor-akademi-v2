using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SporAkademi.Api.Errors;

/// <summary>
/// Unexpected failures become a 500 with <c>Unexpected.Error</c> and the trace id only; no message,
/// stack trace or exception type reaches the client (ADR-0007). Malformed requests become 400s.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var (status, code) = exception switch
        {
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => (StatusCodes.Status400BadRequest, "Request.TooLarge"),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Validation.Failed"),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Request.Cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected.Error"),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            LogUnexpected(logger, exception);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"https://httpstatuses.io/{status}",
        };
        problem.Extensions["code"] = code;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
