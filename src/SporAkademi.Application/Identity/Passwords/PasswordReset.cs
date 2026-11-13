using FluentValidation;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Passwords;

/// <summary>
/// P2-F4 "Şifremi unuttum" (DEC-353). The answer never depends on the address: unknown, deactivated, cooled-down and
/// accepted requests all succeed the same way. An accepted request only writes an outbox message; the link is
/// created and e-mailed after commit. A signed-in caller is ignored (they change the password instead).
/// </summary>
public sealed record RequestPasswordResetCommand(string Email) : ICommand<Unit>;

/// <summary>P2-F4 "Yeni şifre belirle": sets a new password with a set-password link (reset or welcome).</summary>
public sealed record ConfirmPasswordResetCommand(string Token, string NewPassword) : ICommand<Unit>;

internal sealed class RequestPasswordResetValidator : AbstractValidator<RequestPasswordResetCommand>
{
    public RequestPasswordResetValidator() =>
        RuleFor(c => c.Email).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(Email.MaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
}

internal sealed class ConfirmPasswordResetValidator : AbstractValidator<ConfirmPasswordResetCommand>
{
    /// <summary>The tokens are 43 characters; anything much longer is not one.</summary>
    public const int TokenMaxLength = 128;

    public ConfirmPasswordResetValidator()
    {
        RuleFor(c => c.Token).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(TokenMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
        RuleFor(c => c.NewPassword).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(PasswordPolicy.IsStrongEnough).WithErrorCode(AuthErrors.PasswordTooWeak.Code);
    }
}

internal sealed class RequestPasswordResetHandler(
    IUserRepository users,
    ITenancyModule tenancy,
    IOutbox outbox,
    ICurrentUser currentUser,
    TimeProvider timeProvider) : ICommandHandler<RequestPasswordResetCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RequestPasswordResetCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.IsAuthenticated)
        {
            return Result.Success(Unit.Value);
        }

        var email = Email.Create(command.Email);
        var user = email.IsSuccess ? await users.GetByEmailAsync(email.Value, cancellationToken) : null;
        if (user is null
            || (user.TenantId is { } school && !await tenancy.IsSchoolActiveAsync(school, cancellationToken))
            || !user.TryRequestPasswordReset(timeProvider.GetUtcNow()))
        {
            return Result.Success(Unit.Value);
        }

        outbox.Enqueue(new PasswordResetRequestedMessage(user.Id.Value), user.TenantId?.Value);
        return Result.Success(Unit.Value);
    }
}

internal sealed class ConfirmPasswordResetHandler(
    IUserRepository users,
    IPasswordTokenFactory passwordTokens,
    IPasswordHasher passwordHasher,
    ITenancyModule tenancy,
    TimeProvider timeProvider) : ICommandHandler<ConfirmPasswordResetCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ConfirmPasswordResetCommand command, CancellationToken cancellationToken)
    {
        var hash = passwordTokens.Hash(command.Token);
        var user = await users.GetByPasswordTokenHashAsync(hash, cancellationToken);
        // Like the request: a deactivated school's users get no new password (and the answer does not say why).
        if (user is null || (user.TenantId is { } school && !await tenancy.IsSchoolActiveAsync(school, cancellationToken)))
        {
            return Result.Failure<Unit>(AuthErrors.ResetTokenInvalid);
        }

        var changed = user.SetPasswordWithToken(hash, passwordHasher.Hash(command.NewPassword), timeProvider.GetUtcNow());
        return changed.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(changed.Error);
    }
}

/// <summary>P3-02: a committed parent invitation becomes the invitation e-mail message, in the same transaction (DEC-383).</summary>
internal sealed class PublishParentInvited(IOutbox outbox) : IDomainEventConsumer<ParentInvited>
{
    public Task<Result> Handle(ParentInvited domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(new ParentInvitedMessage(domainEvent.UserId.Value), domainEvent.TenantId.Value);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>
/// P4-03 (DEC-512): a password change becomes the notice to the account's owner, in the same transaction. The first
/// password a user sets themselves is not announced.
/// </summary>
internal sealed class PublishPasswordChanged(IOutbox outbox, IUserRepository users) : IDomainEventConsumer<PasswordChanged>
{
    public async Task<Result> Handle(PasswordChanged domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.FirstPassword)
        {
            return Result.Success();
        }

        var user = await users.GetByIdAsync(domainEvent.UserId, cancellationToken);
        if (user is not null)
        {
            outbox.Enqueue(new PasswordChangedMessage(user.Id.Value), user.TenantId?.Value);
        }

        return Result.Success();
    }
}

/// <summary>P2-F4: a committed new account becomes the welcome e-mail message, in the same transaction (DEC-351).</summary>
internal sealed class PublishAccountOpened(IOutbox outbox) : IDomainEventConsumer<UserCreated>
{
    public Task<Result> Handle(UserCreated domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(new AccountOpenedMessage(domainEvent.UserId.Value), domainEvent.TenantId?.Value);
        return Task.FromResult(Result.Success());
    }
}
