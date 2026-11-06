using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Sessions;

/// <summary>US-04: exchanges the refresh token from the cookie for a new token pair. A reuse revokes all sessions, which must commit.</summary>
public sealed record RefreshSessionCommand(string? RefreshToken) : ICommand<AuthSession>, IKeepChangesOnFailure;

internal sealed class RefreshSessionHandler(
    IUserRepository users,
    IRefreshTokenFactory refreshTokens,
    IAccessTokenIssuer accessTokens,
    ITenancyModule tenancy,
    TimeProvider timeProvider) : ICommandHandler<RefreshSessionCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Result.Failure<AuthSession>(AuthErrors.SessionExpired);
        }

        var now = timeProvider.GetUtcNow();
        var presentedHash = refreshTokens.Hash(command.RefreshToken);
        var user = await users.GetByRefreshTokenHashAsync(presentedHash, cancellationToken);
        if (user is null)
        {
            return Result.Failure<AuthSession>(AuthErrors.SessionExpired);
        }

        if (user.TenantId is { } school && !await tenancy.IsSchoolActiveAsync(school, cancellationToken))
        {
            return Result.Failure<AuthSession>(AuthErrors.AccountInactive);
        }

        var next = refreshTokens.Create();
        var rotated = user.RotateRefreshToken(presentedHash, next.Hash, now);
        return rotated.IsSuccess
            ? Result.Success(AuthSessions.Create(accessTokens, user, next, rotated.Value, now))
            : Result.Failure<AuthSession>(rotated.Error);
    }
}

/// <summary>US-04: revokes the presented refresh token. Always succeeds, so logout never fails for the client.</summary>
public sealed record LogoutCommand(string? RefreshToken) : ICommand<Unit>;

internal sealed class LogoutHandler(
    IUserRepository users,
    IRefreshTokenFactory refreshTokens,
    TimeProvider timeProvider) : ICommandHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            var hash = refreshTokens.Hash(command.RefreshToken);
            var user = await users.GetByRefreshTokenHashAsync(hash, cancellationToken);
            user?.RevokeRefreshToken(hash, timeProvider.GetUtcNow());
        }

        return Result.Success(Unit.Value);
    }
}
