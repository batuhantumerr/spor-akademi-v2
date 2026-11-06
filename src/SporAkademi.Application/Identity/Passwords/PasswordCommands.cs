using FluentValidation;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Passwords;

/// <summary>US-05: the signed-in user sets a new password; other sessions end, this one continues with new tokens.</summary>
public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword) : ICommand<AuthSession>;

internal sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(PasswordPolicy.MaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
        RuleFor(c => c.NewPassword).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(PasswordPolicy.IsStrongEnough).WithErrorCode(AuthErrors.PasswordTooWeak.Code);
    }
}

internal sealed class ChangePasswordHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    IRefreshTokenFactory refreshTokens,
    IAccessTokenIssuer accessTokens,
    TimeProvider timeProvider) : ICommandHandler<ChangePasswordCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        if (user is null)
        {
            return Result.Failure<AuthSession>(UserErrors.NotFound);
        }

        if (passwordHasher.Verify(user.PasswordHash, command.CurrentPassword) == PasswordVerification.Failed)
        {
            return Result.Failure<AuthSession>(AuthErrors.CurrentPasswordIncorrect);
        }

        var now = timeProvider.GetUtcNow();
        var refreshToken = refreshTokens.Create();
        var session = user.ChangePassword(passwordHasher.Hash(command.NewPassword), refreshToken.Hash, now);
        return session.IsSuccess
            ? Result.Success(AuthSessions.Create(accessTokens, user, refreshToken, session.Value, now))
            : Result.Failure<AuthSession>(session.Error);
    }
}

/// <summary>
/// US-05: an admin gives a user a new temporary password, returned once. A School Admin resets coaches of
/// their own school; the Platform Admin resets the School Admins of <see cref="SchoolId"/>.
/// Any other target is reported as not found (US-02: no hint that it exists).
/// </summary>
public sealed record ResetUserPasswordCommand(Guid UserId, Guid? SchoolId = null) : ICommand<TemporaryPasswordIssued>;

public sealed record TemporaryPasswordIssued(string TemporaryPassword);

internal sealed class ResetUserPasswordHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : ICommandHandler<ResetUserPasswordCommand, TemporaryPasswordIssued>
{
    public async Task<Result<TemporaryPasswordIssued>> Handle(ResetUserPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        if (user is null || !MayReset(user, command.SchoolId))
        {
            return Result.Failure<TemporaryPasswordIssued>(UserErrors.NotFound);
        }

        var temporaryPassword = TemporaryPassword.Generate();
        user.ResetPassword(passwordHasher.Hash(temporaryPassword), timeProvider.GetUtcNow());
        return Result.Success(new TemporaryPasswordIssued(temporaryPassword));
    }

    private bool MayReset(User user, Guid? schoolId) => currentUser.Role switch
    {
        Role.SchoolAdmin => user.Role == UserRole.Coach
            && currentUser.TenantId is { } tenant
            && user.TenantId?.Value == tenant,
        Role.PlatformAdmin => user.Role == UserRole.SchoolAdmin
            && schoolId is { } school
            && user.TenantId?.Value == school,
        _ => false,
    };
}
