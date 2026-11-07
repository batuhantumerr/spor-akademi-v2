using SporAkademi.Api.Authentication;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Login;
using SporAkademi.Application.Identity.Passwords;
using SporAkademi.Application.Identity.Profile;
using SporAkademi.Application.Identity.Sessions;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Endpoints.Identity;

internal sealed record LoginRequest(string Email, string Password);

internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

internal sealed record PasswordResetRequest(string Email);

internal sealed record PasswordResetConfirmation(string Token, string NewPassword);

/// <summary>Access token for the body (kept in memory by the client); the refresh token only travels in the cookie.</summary>
internal sealed record SessionResponse(string AccessToken, int ExpiresIn, UserSummary User);

/// <summary>Identity endpoints (appendix D 2.1, 2.2, 2.3).</summary>
internal static partial class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/login", Login)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        // DEC-055: no access token, but the refresh cookie plus a header a cross-site form cannot send.
        auth.MapPost("/refresh", Refresh)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .AddEndpointFilter<RequireXRequestedWithFilter>()
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/logout", Logout)
            .AllowAnonymous()
            .AddEndpointFilter<RequireXRequestedWithFilter>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        auth.MapPost("/change-password", ChangePassword)
            .AllowDuringPasswordChange()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // P2-F4 (DEC-353): self-service reset. Anonymous; a well-formed request always answers 202 Accepted, whether
        // or not the address belongs to an account.
        auth.MapPost("/password-reset/request", RequestPasswordReset)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.PasswordResetRequestPolicy)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/password-reset/confirm", ConfirmPasswordReset)
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapGet("/api/v1/me", Me)
            .AllowDuringPasswordChange()
            .Produces<UserSummary>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/v1/coaches/{id:guid}/reset-password", ResetCoachPassword)
            .RequireAuthorization(policy => policy.RequireRole(nameof(Role.SchoolAdmin)))
            .Produces<TemporaryPasswordIssued>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        app.MapPost("/api/v1/platform/schools/{schoolId:guid}/admins/{userId:guid}/reset-password", ResetSchoolAdminPassword)
            .RequireAuthorization(policy => policy.RequireRole(nameof(Role.PlatformAdmin)))
            .Produces<TemporaryPasswordIssued>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        ICommandHandler<LoginCommand, AuthSession> handler,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LoginCommand(request.Email, request.Password), cancellationToken);
        return result.IsSuccess
            ? StartSession(httpContext, result.Value, timeProvider.GetUtcNow())
            : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> Refresh(
        ICommandHandler<RefreshSessionCommand, AuthSession> handler,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RefreshSessionCommand(RefreshCookie.Read(httpContext.Request)), cancellationToken);
        if (result.IsSuccess)
        {
            return StartSession(httpContext, result.Value, timeProvider.GetUtcNow());
        }

        if (result.Error.Type == ErrorType.Unauthorized)
        {
            RefreshCookie.Delete(httpContext.Response);
        }

        return ProblemResults.Problem(result.Error);
    }

    /// <summary>
    /// Always ends the session on the client: the cookie is cleared and 204 returned even when revoking
    /// fails (e.g. a lock timeout); the token then simply expires. The failure is logged by code only.
    /// </summary>
    private static async Task<IResult> Logout(
        ICommandHandler<LogoutCommand, Unit> handler,
        ILoggerFactory loggerFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LogoutCommand(RefreshCookie.Read(httpContext.Request)), cancellationToken);
        if (result.IsFailure)
        {
            LogRevokeFailed(loggerFactory.CreateLogger(typeof(IdentityEndpoints)), result.Error.Code);
        }

        RefreshCookie.Delete(httpContext.Response);
        return TypedResults.NoContent();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Logout could not revoke the refresh token: {ErrorCode}")]
    private static partial void LogRevokeFailed(ILogger logger, string errorCode);

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        ICommandHandler<ChangePasswordCommand, AuthSession> handler,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var command = new ChangePasswordCommand(currentUser.UserId!.Value, request.CurrentPassword, request.NewPassword);
        var result = await handler.Handle(command, cancellationToken);
        return result.IsSuccess
            ? StartSession(httpContext, result.Value, timeProvider.GetUtcNow())
            : ProblemResults.Problem(result.Error);
    }

    /// <summary>
    /// An empty or overlong address answers 400; every well-formed request answers 202, unknown addresses included.
    /// A concurrency conflict or lock timeout can only happen for an existing account (two requests updating its
    /// cooldown at once, e.g. a double click), so they answer 202 as well: the other request was accepted, and a
    /// 409 would reveal that the account exists (DEC-353).
    /// </summary>
    private static async Task<IResult> RequestPasswordReset(
        PasswordResetRequest request,
        ICommandHandler<RequestPasswordResetCommand, Unit> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RequestPasswordResetCommand(request.Email), cancellationToken);
        return result.IsSuccess || result.Error == PersistenceErrors.ConcurrencyConflict || result.Error == PersistenceErrors.LockTimeout
            ? TypedResults.Accepted((string?)null)
            : ProblemResults.Problem(result.Error);
    }

    /// <summary>Sets the new password and ends every session (refresh tokens and the job panel, DEC-332); the user signs in again.</summary>
    private static async Task<IResult> ConfirmPasswordReset(
        PasswordResetConfirmation request,
        ICommandHandler<ConfirmPasswordResetCommand, Unit> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ConfirmPasswordResetCommand(request.Token, request.NewPassword), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> Me(
        IQueryHandler<GetCurrentUserQuery, UserSummary> handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetCurrentUserQuery(currentUser.UserId!.Value), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : ProblemResults.Problem(result.Error);
    }

    private static Task<IResult> ResetCoachPassword(
        Guid id,
        ICommandHandler<ResetUserPasswordCommand, TemporaryPasswordIssued> handler,
        CancellationToken cancellationToken) =>
        ResetPassword(new ResetUserPasswordCommand(id), handler, cancellationToken);

    private static Task<IResult> ResetSchoolAdminPassword(
        Guid schoolId,
        Guid userId,
        ICommandHandler<ResetUserPasswordCommand, TemporaryPasswordIssued> handler,
        CancellationToken cancellationToken) =>
        ResetPassword(new ResetUserPasswordCommand(userId, schoolId), handler, cancellationToken);

    private static async Task<IResult> ResetPassword(
        ResetUserPasswordCommand command,
        ICommandHandler<ResetUserPasswordCommand, TemporaryPasswordIssued> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(command, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : ProblemResults.Problem(result.Error);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<SessionResponse> StartSession(HttpContext httpContext, AuthSession session, DateTimeOffset now)
    {
        RefreshCookie.Append(httpContext.Response, session.RefreshToken, session.RefreshTokenExpiresAt - now);
        var expiresIn = (int)Math.Round((session.AccessToken.ExpiresAt - now).TotalSeconds);
        return TypedResults.Ok(new SessionResponse(session.AccessToken.Value, expiresIn, session.User));
    }
}

/// <summary>The refresh token cookie (DEC-053): unreadable by scripts, HTTPS only, same site, auth paths only.</summary>
internal static class RefreshCookie
{
    public const string Name = "sa_refresh";
    public const string Path = "/api/v1/auth";

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static void Append(HttpResponse response, string token, TimeSpan maxAge) =>
        response.Cookies.Append(Name, token, Options(maxAge));

    public static void Delete(HttpResponse response) => response.Cookies.Delete(Name, Options(maxAge: null));

    private static CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = maxAge,
        IsEssential = true,
    };
}

/// <summary>DEC-055: cookie-authenticated endpoints require <c>X-Requested-With: XMLHttpRequest</c>, which cross-site forms cannot set.</summary>
internal sealed class RequireXRequestedWithFilter : IEndpointFilter
{
    public const string HeaderName = "X-Requested-With";
    public const string ExpectedValue = "XMLHttpRequest";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        return string.Equals(context.HttpContext.Request.Headers[HeaderName].ToString(), ExpectedValue, StringComparison.Ordinal)
            ? next(context)
            : ValueTask.FromResult<object?>(ProblemResults.Problem(ValidationErrors.ForField(HeaderName, CommonErrors.FieldRequired)));
    }
}
