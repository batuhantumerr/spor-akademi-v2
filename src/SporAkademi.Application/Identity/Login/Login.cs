using FluentValidation;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Login;

/// <summary>US-03. Failures still commit: a wrong password counts towards the lockout.</summary>
public sealed record LoginCommand(string Email, string Password) : ICommand<AuthSession>, IKeepChangesOnFailure;

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(Email.MaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
        RuleFor(c => c.Password).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(PasswordPolicy.MaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
    }
}

internal sealed class LoginHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    IRefreshTokenFactory refreshTokens,
    IAccessTokenIssuer accessTokens,
    ITenancyModule tenancy,
    TimeProvider timeProvider) : ICommandHandler<LoginCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var email = Email.Create(command.Email);
        var user = email.IsSuccess ? await users.GetByEmailAsync(email.Value, cancellationToken) : null;
        if (user is null)
        {
            // Same answer and similar timing as a wrong password: no user enumeration.
            passwordHasher.SpendVerificationTime(command.Password);
            return Result.Failure<AuthSession>(AuthErrors.InvalidCredentials);
        }

        var notLocked = user.CheckNotLockedOut(now);
        if (notLocked.IsFailure)
        {
            return Result.Failure<AuthSession>(notLocked.Error);
        }

        var verification = passwordHasher.Verify(user.PasswordHash, command.Password);
        if (verification == PasswordVerification.Failed)
        {
            return Result.Failure<AuthSession>(user.RecordFailedLogin(now));
        }

        // Status is only revealed to someone who knows the password.
        if (user.TenantId is { } school && !await tenancy.IsSchoolActiveAsync(school, cancellationToken))
        {
            return Result.Failure<AuthSession>(AuthErrors.AccountInactive);
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(passwordHasher.Hash(command.Password));
        }

        var refreshToken = refreshTokens.Create();
        var stored = user.CompleteLogin(refreshToken.Hash, now);
        return stored.IsSuccess
            ? Result.Success(AuthSessions.Create(accessTokens, user, refreshToken, stored.Value, now))
            : Result.Failure<AuthSession>(stored.Error);
    }
}
