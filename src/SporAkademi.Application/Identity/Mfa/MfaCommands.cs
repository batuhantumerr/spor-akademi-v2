using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Auditing;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Mfa;

internal static class MfaValidation
{
    public const int MaxCodeLength = 32;
    public const int MaxTokenLength = 128;

    public static IRuleBuilderOptions<T, string> Code<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(MaxCodeLength).WithErrorCode(CommonErrors.FieldTooLong.Code);

    public static IRuleBuilderOptions<T, string> Token<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(MaxTokenLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
}

// ---- Signed-in user: status, enrol, disable -------------------------------------------------------------------------

public sealed record GetMfaStatusQuery(Guid UserId) : IQuery<MfaStatus>;

internal sealed class GetMfaStatusHandler(IUserRepository users, IMfaPolicy policy) : IQueryHandler<GetMfaStatusQuery, MfaStatus>
{
    public async Task<Result<MfaStatus>> Handle(GetMfaStatusQuery query, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(query.UserId), cancellationToken);
        return user is null
            ? Result.Failure<MfaStatus>(UserErrors.NotFound)
            : Result.Success(new MfaStatus(User.MayUseMfa(user.Role), user.MfaEnabled, policy.IsRequired(user.Role), user.RecoveryCodesRemaining));
    }
}

/// <summary>Starts (or restarts) the signed-in user's enrolment: a new secret, shown as a QR code and as text.</summary>
public sealed record StartMfaEnrollmentCommand(Guid UserId) : ICommand<MfaEnrollmentStarted>;

internal sealed class StartMfaEnrollmentHandler(IUserRepository users, MfaEnrollment enrollment, TimeProvider timeProvider)
    : ICommandHandler<StartMfaEnrollmentCommand, MfaEnrollmentStarted>
{
    public async Task<Result<MfaEnrollmentStarted>> Handle(StartMfaEnrollmentCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        return user is null
            ? Result.Failure<MfaEnrollmentStarted>(UserErrors.NotFound)
            : enrollment.Start(user, timeProvider.GetUtcNow());
    }
}

/// <summary>Confirms the signed-in user's enrolment with a code from the app. A wrong code counts towards the lockout, so failures commit.</summary>
public sealed record ConfirmMfaEnrollmentCommand(Guid UserId, string Code) : ICommand<MfaRecoveryCodes>, IKeepChangesOnFailure;

internal sealed class ConfirmMfaEnrollmentValidator : AbstractValidator<ConfirmMfaEnrollmentCommand>
{
    public ConfirmMfaEnrollmentValidator() => RuleFor(c => c.Code).Code();
}

internal sealed class ConfirmMfaEnrollmentHandler(IUserRepository users, MfaEnrollment enrollment, TimeProvider timeProvider)
    : ICommandHandler<ConfirmMfaEnrollmentCommand, MfaRecoveryCodes>
{
    public async Task<Result<MfaRecoveryCodes>> Handle(ConfirmMfaEnrollmentCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        if (user is null)
        {
            return Result.Failure<MfaRecoveryCodes>(UserErrors.NotFound);
        }

        var now = timeProvider.GetUtcNow();
        var notLocked = user.CheckNotLockedOut(now);
        if (notLocked.IsFailure)
        {
            return Result.Failure<MfaRecoveryCodes>(notLocked.Error);
        }

        var confirmed = enrollment.Confirm(user, command.Code, now);
        return confirmed.IsFailure && confirmed.Error == MfaErrors.CodeInvalid
            ? Result.Failure<MfaRecoveryCodes>(user.RecordMfaFailure(null, now))
            : confirmed;
    }
}

/// <summary>
/// Turns the signed-in user's MFA off, with a current code (or a recovery code). Not for a role that must have it.
/// A wrong code counts towards the lockout, so failures commit.
/// </summary>
public sealed record DisableMfaCommand(Guid UserId, string Code) : ICommand<Unit>, IKeepChangesOnFailure;

internal sealed class DisableMfaValidator : AbstractValidator<DisableMfaCommand>
{
    public DisableMfaValidator() => RuleFor(c => c.Code).Code();
}

internal sealed class DisableMfaHandler(IUserRepository users, IMfaPolicy policy, MfaCodeVerifier verifier, TimeProvider timeProvider)
    : ICommandHandler<DisableMfaCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DisableMfaCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        if (user is null)
        {
            return Result.Failure<Unit>(UserErrors.NotFound);
        }

        if (!user.MfaEnabled)
        {
            return Result.Failure<Unit>(MfaErrors.NotEnabled);
        }

        if (policy.IsRequired(user.Role))
        {
            return Result.Failure<Unit>(MfaErrors.RequiredForRole);
        }

        var now = timeProvider.GetUtcNow();
        var notLocked = user.CheckNotLockedOut(now);
        if (notLocked.IsFailure)
        {
            return Result.Failure<Unit>(notLocked.Error);
        }

        var checkedCode = verifier.Verify(user, command.Code, now);
        if (checkedCode.IsFailure)
        {
            return Result.Failure<Unit>(checkedCode.Error == MfaErrors.SecretUnreadable ? checkedCode.Error : user.RecordMfaFailure(null, now));
        }

        var disabled = user.DisableMfa(now);
        return disabled.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(disabled.Error);
    }
}

/// <summary>
/// Replaces the signed-in user's recovery codes with a new set, after a current code from the authenticator (V1-03,
/// DEC-687). The old codes stop working at once, the new ones are shown once, and the owner is told by e-mail. A wrong
/// code counts towards the lockout, so failures commit.
/// </summary>
public sealed record RegenerateMfaRecoveryCodesCommand(Guid UserId, string Code) : ICommand<MfaRecoveryCodes>, IKeepChangesOnFailure;

internal sealed class RegenerateMfaRecoveryCodesValidator : AbstractValidator<RegenerateMfaRecoveryCodesCommand>
{
    public RegenerateMfaRecoveryCodesValidator() => RuleFor(c => c.Code).Code();
}

internal sealed class RegenerateMfaRecoveryCodesHandler(IUserRepository users, MfaCodeVerifier verifier, TimeProvider timeProvider)
    : ICommandHandler<RegenerateMfaRecoveryCodesCommand, MfaRecoveryCodes>
{
    public async Task<Result<MfaRecoveryCodes>> Handle(RegenerateMfaRecoveryCodesCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        if (user is null)
        {
            return Result.Failure<MfaRecoveryCodes>(UserErrors.NotFound);
        }

        if (!user.MfaEnabled)
        {
            return Result.Failure<MfaRecoveryCodes>(MfaErrors.NotEnabled);
        }

        var now = timeProvider.GetUtcNow();
        var notLocked = user.CheckNotLockedOut(now);
        if (notLocked.IsFailure)
        {
            return Result.Failure<MfaRecoveryCodes>(notLocked.Error);
        }

        var checkedCode = verifier.VerifyTotp(user, command.Code, now);
        if (checkedCode.IsFailure)
        {
            return Result.Failure<MfaRecoveryCodes>(checkedCode.Error == MfaErrors.SecretUnreadable ? checkedCode.Error : user.RecordMfaFailure(null, now));
        }

        var codes = MfaCodes.NewRecoveryCodes();
        var regenerated = user.RegenerateMfaRecoveryCodes([.. codes.Select(c => MfaCodes.HashRecoveryCode(user.Id, MfaCodes.Normalize(c)))], now);
        return regenerated.IsSuccess
            ? Result.Success(new MfaRecoveryCodes(codes))
            : Result.Failure<MfaRecoveryCodes>(regenerated.Error);
    }
}

// ---- Platform Admin: reset someone's MFA ----------------------------------------------------------------------------

/// <summary>A Platform Admin as the platform screen lists them (V1-03, DEC-688); no hashes, tokens or secrets.</summary>
public sealed record PlatformAdminSummary(Guid Id, string FirstName, string LastName, string Email, UserStatus Status, bool MfaEnabled);

/// <summary>The Platform Admins, so that one can reset another's two-step verification from the screen (V1-03, DEC-688).</summary>
public sealed record ListPlatformAdminsQuery : IQuery<IReadOnlyList<PlatformAdminSummary>>;

internal sealed class ListPlatformAdminsHandler(IUserRepository users, ICurrentUser currentUser)
    : IQueryHandler<ListPlatformAdminsQuery, IReadOnlyList<PlatformAdminSummary>>
{
    public async Task<Result<IReadOnlyList<PlatformAdminSummary>>> Handle(ListPlatformAdminsQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.Role != Role.PlatformAdmin)
        {
            return Result.Failure<IReadOnlyList<PlatformAdminSummary>>(UserErrors.NotFound);
        }

        var admins = await users.ListPlatformAdminsAsync(cancellationToken);
        return Result.Success<IReadOnlyList<PlatformAdminSummary>>(
            [.. admins.Select(u => new PlatformAdminSummary(u.Id.Value, u.FirstName, u.LastName, u.Email.Value, u.Status, u.MfaEnabled))]);
    }
}

/// <summary>
/// A Platform Admin removes another user's MFA (a lost phone and no recovery code, DEC-605). <paramref name="Reason"/> is a
/// <see cref="MfaResetReason"/> name and goes to the audit log; the user's sessions end and they are told by e-mail.
/// </summary>
public sealed record ResetMfaCommand(Guid UserId, string Reason) : ICommand<Unit>;

internal sealed class ResetMfaValidator : AbstractValidator<ResetMfaCommand>
{
    public ResetMfaValidator() =>
        RuleFor(c => c.Reason).Must(EnumNames.IsExact<MfaResetReason>).WithErrorCode(CommonErrors.FieldInvalid.Code);
}

internal sealed class ResetMfaHandler(
    IUserRepository users,
    ICurrentUser currentUser,
    IAuditTrail audit,
    TimeProvider timeProvider) : ICommandHandler<ResetMfaCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ResetMfaCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.Role != Role.PlatformAdmin)
        {
            return Result.Failure<Unit>(UserErrors.NotFound);
        }

        if (currentUser.UserId == command.UserId)
        {
            return Result.Failure<Unit>(MfaErrors.CannotResetOwn);
        }

        var user = await users.GetByIdAsync(UserId.From(command.UserId), cancellationToken);
        if (user is null || !User.MayUseMfa(user.Role))
        {
            return Result.Failure<Unit>(UserErrors.NotFound);
        }

        var reset = user.ResetMfa(timeProvider.GetUtcNow());
        if (reset.IsFailure)
        {
            return Result.Failure<Unit>(reset.Error);
        }

        // The change of the user's MfaEnabled flag is audited on its own (DEC-534); this names the reason (DEC-533).
        // Another Platform Admin has no school, so the entry is stored without one (V1-03, DEC-689); only platform scope reads it.
        audit.Add(new AuditRecord(AuditAction.Update, AuditEntityType.User, user.Id.Value, [nameof(User.MfaEnabled)], command.Reason, PlatformLevel: user.TenantId is null));
        return Result.Success(Unit.Value);
    }
}

// ---- Login second step: the challenge token is the credential --------------------------------------------------------

/// <summary>Finds the user and the usable challenge a token stands for, and applies the checks a login makes after the password.</summary>
internal sealed class MfaChallengeLookup(
    IUserRepository users,
    IRefreshTokenFactory tokens,
    ITenancyModule tenancy,
    TimeProvider timeProvider)
{
    public async Task<Result<(User User, MfaChallenge Challenge)>> FindAsync(string token, MfaChallengePurpose purpose, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = tokens.Hash(token);
        var user = await users.GetByMfaChallengeHashAsync(hash, cancellationToken);
        if (user is null)
        {
            return Result.Failure<(User, MfaChallenge)>(MfaErrors.ChallengeInvalid);
        }

        var notLocked = user.CheckNotLockedOut(now);
        if (notLocked.IsFailure)
        {
            return Result.Failure<(User, MfaChallenge)>(notLocked.Error);
        }

        var challenge = user.FindMfaChallenge(hash, purpose, now);
        if (challenge.IsFailure)
        {
            return Result.Failure<(User, MfaChallenge)>(challenge.Error);
        }

        if (user.Status != UserStatus.Active
            || (user.TenantId is { } school && !await tenancy.IsSchoolActiveAsync(school, cancellationToken)))
        {
            return Result.Failure<(User, MfaChallenge)>(AuthErrors.AccountInactive);
        }

        return Result.Success((user, challenge.Value));
    }
}

/// <summary>Completes a login with the code of the user's authenticator, or a recovery code. A wrong code counts, so failures commit.</summary>
public sealed record VerifyMfaCommand(string Token, string Code) : ICommand<AuthSession>, IKeepChangesOnFailure;

internal sealed class VerifyMfaValidator : AbstractValidator<VerifyMfaCommand>
{
    public VerifyMfaValidator()
    {
        RuleFor(c => c.Token).Token();
        RuleFor(c => c.Code).Code();
    }
}

internal sealed class VerifyMfaHandler(
    MfaChallengeLookup lookup,
    MfaCodeVerifier verifier,
    IRefreshTokenFactory refreshTokens,
    IAccessTokenIssuer accessTokens,
    ISessionPolicies sessionPolicies,
    TimeProvider timeProvider) : ICommandHandler<VerifyMfaCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(VerifyMfaCommand command, CancellationToken cancellationToken)
    {
        var found = await lookup.FindAsync(command.Token, MfaChallengePurpose.Verify, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<AuthSession>(found.Error);
        }

        var (user, challenge) = found.Value;
        var now = timeProvider.GetUtcNow();
        var checkedCode = verifier.Verify(user, command.Code, now);
        if (checkedCode.IsFailure)
        {
            return Result.Failure<AuthSession>(checkedCode.Error == MfaErrors.SecretUnreadable ? checkedCode.Error : user.RecordMfaFailure(challenge, now));
        }

        user.CompleteMfaChallenge(challenge);
        var refreshToken = refreshTokens.Create();
        var stored = user.CompleteLogin(refreshToken.Hash, now, sessionPolicies.ForRole(user.Role));
        return stored.IsSuccess
            ? Result.Success(AuthSessions.Create(accessTokens, user, refreshToken, stored.Value, now))
            : Result.Failure<AuthSession>(stored.Error);
    }
}

/// <summary>The first enrolment of a role that must have MFA, before it has a session (DEC-606): a new secret for the QR code.</summary>
public sealed record StartMfaEnrollmentWithChallengeCommand(string Token) : ICommand<MfaEnrollmentStarted>;

internal sealed class StartMfaEnrollmentWithChallengeValidator : AbstractValidator<StartMfaEnrollmentWithChallengeCommand>
{
    public StartMfaEnrollmentWithChallengeValidator() => RuleFor(c => c.Token).Token();
}

internal sealed class StartMfaEnrollmentWithChallengeHandler(MfaChallengeLookup lookup, MfaEnrollment enrollment, TimeProvider timeProvider)
    : ICommandHandler<StartMfaEnrollmentWithChallengeCommand, MfaEnrollmentStarted>
{
    public async Task<Result<MfaEnrollmentStarted>> Handle(StartMfaEnrollmentWithChallengeCommand command, CancellationToken cancellationToken)
    {
        var found = await lookup.FindAsync(command.Token, MfaChallengePurpose.Enroll, cancellationToken);
        return found.IsFailure
            ? Result.Failure<MfaEnrollmentStarted>(found.Error)
            : enrollment.Start(found.Value.User, timeProvider.GetUtcNow());
    }
}

/// <summary>The enrolled user's session and the recovery codes shown to them once.</summary>
public sealed record MfaEnrollmentCompleted(AuthSession Session, MfaRecoveryCodes RecoveryCodes);

/// <summary>Confirms the first enrolment with a code, and completes the login. A wrong code counts, so failures commit.</summary>
public sealed record CompleteMfaEnrollmentWithChallengeCommand(string Token, string Code) : ICommand<MfaEnrollmentCompleted>, IKeepChangesOnFailure;

internal sealed class CompleteMfaEnrollmentWithChallengeValidator : AbstractValidator<CompleteMfaEnrollmentWithChallengeCommand>
{
    public CompleteMfaEnrollmentWithChallengeValidator()
    {
        RuleFor(c => c.Token).Token();
        RuleFor(c => c.Code).Code();
    }
}

internal sealed class CompleteMfaEnrollmentWithChallengeHandler(
    MfaChallengeLookup lookup,
    MfaEnrollment enrollment,
    IRefreshTokenFactory refreshTokens,
    IAccessTokenIssuer accessTokens,
    ISessionPolicies sessionPolicies,
    TimeProvider timeProvider) : ICommandHandler<CompleteMfaEnrollmentWithChallengeCommand, MfaEnrollmentCompleted>
{
    public async Task<Result<MfaEnrollmentCompleted>> Handle(CompleteMfaEnrollmentWithChallengeCommand command, CancellationToken cancellationToken)
    {
        var found = await lookup.FindAsync(command.Token, MfaChallengePurpose.Enroll, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<MfaEnrollmentCompleted>(found.Error);
        }

        var (user, challenge) = found.Value;
        var now = timeProvider.GetUtcNow();
        var confirmed = enrollment.Confirm(user, command.Code, now);
        if (confirmed.IsFailure)
        {
            return Result.Failure<MfaEnrollmentCompleted>(confirmed.Error == MfaErrors.CodeInvalid ? user.RecordMfaFailure(challenge, now) : confirmed.Error);
        }

        user.CompleteMfaChallenge(challenge);
        var refreshToken = refreshTokens.Create();
        var stored = user.CompleteLogin(refreshToken.Hash, now, sessionPolicies.ForRole(user.Role));
        return stored.IsSuccess
            ? Result.Success(new MfaEnrollmentCompleted(AuthSessions.Create(accessTokens, user, refreshToken, stored.Value, now), confirmed.Value))
            : Result.Failure<MfaEnrollmentCompleted>(stored.Error);
    }
}
