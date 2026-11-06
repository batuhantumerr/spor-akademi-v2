using SporAkademi.Api.Authentication;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
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

/// <summary>Access token for the body (kept in memory by the client); the refresh token only travels in the cookie.</summary>
internal sealed record SessionResponse(string AccessToken, int ExpiresIn, UserSummary User);

/// <summary>Identity endpoints (appendix D 2.1, 2.2, 2.3).</summary>
internal static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(LoginRateLimiting.Policy);
        // DEC-055: no access token, but the refresh cookie plus a header a cross-site form cannot send.
        auth.MapPost("/refresh", Refresh).AllowAnonymous().AddEndpointFilter<RequireXRequestedWithFilter>();
        auth.MapPost("/logout", Logout).AllowAnonymous().AddEndpointFilter<RequireXRequestedWithFilter>();
        auth.MapPost("/change-password", ChangePassword).AllowDuringPasswordChange();

        app.MapGet("/api/v1/me", Me).AllowDuringPasswordChange();

        app.MapPost("/api/v1/coaches/{id:guid}/reset-password", ResetCoachPassword)
            .RequireAuthorization(policy => policy.RequireRole(nameof(Role.SchoolAdmin)));
        app.MapPost("/api/v1/platform/schools/{schoolId:guid}/admins/{userId:guid}/reset-password", ResetSchoolAdminPassword)
            .RequireAuthorization(policy => policy.RequireRole(nameof(Role.PlatformAdmin)));

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

    private static async Task<IResult> Logout(
        ICommandHandler<LogoutCommand, Unit> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LogoutCommand(RefreshCookie.Read(httpContext.Request)), cancellationToken);
        if (result.IsFailure)
        {
            return ProblemResults.Problem(result.Error);
        }

        RefreshCookie.Delete(httpContext.Response);
        return TypedResults.NoContent();
    }

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
