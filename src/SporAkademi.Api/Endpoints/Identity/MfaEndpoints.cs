using SporAkademi.Api.Authentication;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Mfa;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Endpoints.Identity;

internal sealed record VerifyMfaRequest(string MfaToken, string Code);

internal sealed record MfaEnrollmentChallengeRequest(string MfaToken);

internal sealed record MfaCodeRequest(string Code);

internal sealed record ResetMfaRequest(string Reason);

/// <summary>
/// The answer of a login whose password was right but whose second step is still due (<c>202</c>): <c>Purpose</c> is
/// <c>Verify</c> (ask for a code) or <c>Enroll</c> (the role must set MFA up first). The token is good for five minutes, once.
/// </summary>
internal sealed record MfaChallengeResponse(string Purpose, string MfaToken, int ExpiresIn)
{
    public static MfaChallengeResponse From(MfaChallengeIssued challenge, DateTimeOffset now) =>
        new(challenge.Purpose.ToString(), challenge.Token, (int)Math.Round((challenge.ExpiresAt - now).TotalSeconds));
}

/// <summary>A finished first enrolment: the session of <see cref="SessionResponse"/> and the recovery codes, shown once.</summary>
internal sealed record MfaEnrollmentSessionResponse(string AccessToken, int ExpiresIn, UserSummary User, IReadOnlyList<string> RecoveryCodes);

/// <summary>Two-step verification (P5-01, appendix D 2.4).</summary>
internal static partial class IdentityEndpoints
{
    private static IEndpointRouteBuilder MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        // The second step of a login: the challenge token is the credential, so these are anonymous and rate limited.
        var auth = app.MapGroup("/api/v1/auth/mfa");
        auth.MapPost("/verify", VerifyMfa)
            .AllowAnonymous()
            .UsesPlatformData()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<SessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/enrollment/start", StartEnrollmentWithChallenge)
            .AllowAnonymous()
            .UsesPlatformData()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<MfaEnrollmentStarted>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/enrollment/confirm", ConfirmEnrollmentWithChallenge)
            .AllowAnonymous()
            .UsesPlatformData()
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<MfaEnrollmentSessionResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // The signed-in user's own settings: staff of the admin panel with their own reach.
        var me = app.MapGroup("/api/v1/me/mfa")
            .RequireAuthorization(policy => policy.RequireRole(nameof(Role.SchoolAdmin), nameof(Role.PlatformAdmin)));
        me.MapGet(string.Empty, GetMfaStatus)
            .Produces<MfaStatus>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        me.MapPost("/enrollment", StartEnrollment)
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<MfaEnrollmentStarted>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        me.MapPost("/enrollment/confirm", ConfirmEnrollment)
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<MfaRecoveryCodes>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        me.MapPost("/recovery-codes", RegenerateRecoveryCodes)
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces<MfaRecoveryCodes>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        me.MapPost("/disable", DisableMfa)
            .RequireRateLimiting(AuthRateLimiting.Policy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // A lost phone and no recovery code (DEC-605): a Platform Admin takes MFA off another account.
        app.MapPost("/api/v1/platform/users/{userId:guid}/mfa/reset", ResetMfa)
            .RequireAuthorization(policy => policy.RequireRole(nameof(Role.PlatformAdmin)))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<IResult> VerifyMfa(
        VerifyMfaRequest request,
        ICommandHandler<VerifyMfaCommand, AuthSession> handler,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new VerifyMfaCommand(request.MfaToken, request.Code), cancellationToken);
        if (result.IsFailure)
        {
            // Like a wrong password (P4-07, DEC-552): by error code only.
            AppTelemetry.LoginFailures.Add(1, new KeyValuePair<string, object?>("reason", result.Error.Code));
        }

        return result.IsSuccess
            ? StartSession(httpContext, result.Value, timeProvider.GetUtcNow())
            : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> StartEnrollmentWithChallenge(
        MfaEnrollmentChallengeRequest request,
        ICommandHandler<StartMfaEnrollmentWithChallengeCommand, MfaEnrollmentStarted> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new StartMfaEnrollmentWithChallengeCommand(request.MfaToken), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ConfirmEnrollmentWithChallenge(
        VerifyMfaRequest request,
        ICommandHandler<CompleteMfaEnrollmentWithChallengeCommand, MfaEnrollmentCompleted> handler,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CompleteMfaEnrollmentWithChallengeCommand(request.MfaToken, request.Code), cancellationToken);
        if (result.IsFailure)
        {
            AppTelemetry.LoginFailures.Add(1, new KeyValuePair<string, object?>("reason", result.Error.Code));
            return ProblemResults.Problem(result.Error);
        }

        var now = timeProvider.GetUtcNow();
        var session = result.Value.Session;
        RefreshCookie.Append(httpContext.Response, session.RefreshToken, session.RefreshTokenExpiresAt - now);
        var expiresIn = (int)Math.Round((session.AccessToken.ExpiresAt - now).TotalSeconds);
        return TypedResults.Ok(new MfaEnrollmentSessionResponse(session.AccessToken.Value, expiresIn, session.User, result.Value.RecoveryCodes.Codes));
    }

    private static async Task<IResult> GetMfaStatus(
        IQueryHandler<GetMfaStatusQuery, MfaStatus> handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetMfaStatusQuery(currentUser.UserId!.Value), cancellationToken)).OkOrProblem();

    private static async Task<IResult> StartEnrollment(
        ICommandHandler<StartMfaEnrollmentCommand, MfaEnrollmentStarted> handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new StartMfaEnrollmentCommand(currentUser.UserId!.Value), cancellationToken)).OkOrProblem();

    private static async Task<IResult> ConfirmEnrollment(
        MfaCodeRequest request,
        ICommandHandler<ConfirmMfaEnrollmentCommand, MfaRecoveryCodes> handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ConfirmMfaEnrollmentCommand(currentUser.UserId!.Value, request.Code), cancellationToken)).OkOrProblem();

    private static async Task<IResult> RegenerateRecoveryCodes(
        MfaCodeRequest request,
        ICommandHandler<RegenerateMfaRecoveryCodesCommand, MfaRecoveryCodes> handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new RegenerateMfaRecoveryCodesCommand(currentUser.UserId!.Value, request.Code), cancellationToken)).OkOrProblem();

    private static async Task<IResult> DisableMfa(
        MfaCodeRequest request,
        ICommandHandler<DisableMfaCommand, Unit> handler,
        ICurrentUser currentUser,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new DisableMfaCommand(currentUser.UserId!.Value, request.Code), cancellationToken)).NoContentOrProblem();

    private static async Task<IResult> ResetMfa(
        Guid userId,
        ResetMfaRequest request,
        ICommandHandler<ResetMfaCommand, Unit> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ResetMfaCommand(userId, request.Reason), cancellationToken)).NoContentOrProblem();
}
