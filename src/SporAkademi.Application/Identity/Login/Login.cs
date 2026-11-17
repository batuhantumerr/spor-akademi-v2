using FluentValidation;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity.Mfa;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Login;

/// <summary>
/// US-03. Failures still commit: a wrong password counts towards the lockout. With MFA (P5-01) a right password ends in a
/// <see cref="LoginOutcome"/> with a challenge instead of a session.
/// </summary>
public sealed record LoginCommand(string Email, string Password) : ICommand<LoginOutcome>, IKeepChangesOnFailure;

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
    IMfaPolicy mfaPolicy,
    TimeProvider timeProvider) : ICommandHandler<LoginCommand, LoginOutcome>
{
    public async Task<Result<LoginOutcome>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var email = Email.Create(command.Email);
        var user = email.IsSuccess ? await users.GetByEmailAsync(email.Value, cancellationToken) : null;
        if (user is null)
        {
            // Same answer and similar timing as a wrong password: no user enumeration.
            passwordHasher.SpendVerificationTime(command.Password);
            return Result.Failure<LoginOutcome>(AuthErrors.InvalidCredentials);
        }

        var notLocked = user.CheckNotLockedOut(now);
        if (notLocked.IsFailure)
        {
            return Result.Failure<LoginOutcome>(notLocked.Error);
        }

        var verification = passwordHasher.Verify(user.PasswordHash, command.Password);
        if (verification == PasswordVerification.Failed)
        {
            return Result.Failure<LoginOutcome>(user.RecordFailedLogin(now));
        }

        // Status is only revealed to someone who knows the password.
        if (user.TenantId is { } school && !await tenancy.IsSchoolActiveAsync(school, cancellationToken))
        {
            return Result.Failure<LoginOutcome>(AuthErrors.AccountInactive);
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(passwordHasher.Hash(command.Password));
        }

        // P5-01: the password alone is not enough for an account with MFA, or for a role that must have it. The failure
        // counters stay as they are: only a finished login clears them, so wrong codes keep counting across attempts.
        MfaChallengePurpose? secondStep = user.MfaEnabled ? MfaChallengePurpose.Verify
            : mfaPolicy.IsRequired(user.Role) ? MfaChallengePurpose.Enroll
            : null;
        if (secondStep is { } purpose)
        {
            if (user.Status != UserStatus.Active)
            {
                return Result.Failure<LoginOutcome>(AuthErrors.AccountInactive);
            }

            var token = refreshTokens.Create();
            var challenge = user.IssueMfaChallenge(token.Hash, purpose, now);
            return Result.Success(LoginOutcome.SecondStep(new MfaChallengeIssued(token.Token, challenge.ExpiresAt, purpose)));
        }

        var refreshToken = refreshTokens.Create();
        var stored = user.CompleteLogin(refreshToken.Hash, now);
        return stored.IsSuccess
            ? Result.Success(LoginOutcome.Authenticated(AuthSessions.Create(accessTokens, user, refreshToken, stored.Value, now)))
            : Result.Failure<LoginOutcome>(stored.Error);
    }
}
