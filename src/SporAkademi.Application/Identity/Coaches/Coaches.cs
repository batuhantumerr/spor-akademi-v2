using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Coaches;

/// <summary>US-08: coach accounts of the caller's school. Coaches of other schools are not found (US-02).</summary>
public sealed record CreateCoachCommand(string FirstName, string LastName, string Email, string? Phone) : ICommand<CoachCreated>;

public sealed record CoachCreated(Guid CoachId, string TemporaryPassword);

public sealed record UpdateCoachCommand(Guid Id, string FirstName, string LastName, string? Phone, uint Version) : ICommand<Unit>;

public sealed record DeactivateCoachCommand(Guid Id) : ICommand<Unit>;

public sealed record GetCoachQuery(Guid Id) : IQuery<CoachDto>;

public sealed record ListCoachesQuery(PageRequest Page) : IQuery<PagedResult<CoachDto>>;

internal static class CoachRules
{
    public static IRuleBuilderOptions<T, string> ValidName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(User.NameMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);

    /// <summary>Empty means no phone; anything else must be a valid Turkish number.</summary>
    public static Result<PhoneNumber?> ParsePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return Result.Success<PhoneNumber?>(null);
        }

        var parsed = PhoneNumber.Create(phone);
        return parsed.IsSuccess
            ? Result.Success<PhoneNumber?>(parsed.Value)
            : Result.Failure<PhoneNumber?>(ValidationErrors.ForField("phone", parsed.Error));
    }
}

internal sealed class CreateCoachValidator : AbstractValidator<CreateCoachCommand>
{
    public CreateCoachValidator()
    {
        RuleFor(c => c.FirstName).ValidName();
        RuleFor(c => c.LastName).ValidName();
        RuleFor(c => c.Email).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(e => Email.Create(e).IsSuccess).WithErrorCode(Email.Invalid.Code);
        RuleFor(c => c.Phone).Must(p => CoachRules.ParsePhone(p).IsSuccess).WithErrorCode(PhoneNumber.Invalid.Code);
    }
}

internal sealed class UpdateCoachValidator : AbstractValidator<UpdateCoachCommand>
{
    public UpdateCoachValidator()
    {
        RuleFor(c => c.FirstName).ValidName();
        RuleFor(c => c.LastName).ValidName();
        RuleFor(c => c.Phone).Must(p => CoachRules.ParsePhone(p).IsSuccess).WithErrorCode(PhoneNumber.Invalid.Code);
        RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode(CommonErrors.FieldRequired.Code);
    }
}

internal sealed class ListCoachesValidator : AbstractValidator<ListCoachesQuery>
{
    public ListCoachesValidator() => RuleFor(q => q.Page).SetValidator(new PageRequestValidator());
}

internal sealed class CreateCoachHandler(AccountOpening accounts, ICurrentUser currentUser) : ICommandHandler<CreateCoachCommand, CoachCreated>
{
    public async Task<Result<CoachCreated>> Handle(CreateCoachCommand command, CancellationToken cancellationToken)
    {
        var phone = CoachRules.ParsePhone(command.Phone);
        if (phone.IsFailure)
        {
            return Result.Failure<CoachCreated>(phone.Error);
        }

        var opened = await accounts.OpenAsync(
            currentUser.RequireTenant(), UserRole.Coach, command.FirstName, command.LastName, command.Email, phone.Value, cancellationToken);
        return opened.IsSuccess
            ? Result.Success(new CoachCreated(opened.Value.User.Id.Value, opened.Value.TemporaryPassword))
            : Result.Failure<CoachCreated>(opened.Error);
    }
}

internal sealed class UpdateCoachHandler(IUserRepository users, ICurrentUser currentUser) : ICommandHandler<UpdateCoachCommand, Unit>
{
    public async Task<Result<Unit>> Handle(UpdateCoachCommand command, CancellationToken cancellationToken)
    {
        var coach = await users.GetCoachAsync(currentUser.RequireTenant(), UserId.From(command.Id), cancellationToken);
        if (coach is null)
        {
            return Result.Failure<Unit>(UserErrors.NotFound);
        }

        var phone = CoachRules.ParsePhone(command.Phone);
        if (phone.IsFailure)
        {
            return Result.Failure<Unit>(phone.Error);
        }

        users.ExpectVersion(coach, command.Version);
        var updated = coach.UpdateProfile(command.FirstName, command.LastName, phone.Value);
        return updated.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(updated.Error);
    }
}

internal sealed class DeactivateCoachHandler(IUserRepository users, ICurrentUser currentUser, TimeProvider timeProvider)
    : ICommandHandler<DeactivateCoachCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeactivateCoachCommand command, CancellationToken cancellationToken)
    {
        var coach = await users.GetCoachAsync(currentUser.RequireTenant(), UserId.From(command.Id), cancellationToken);
        if (coach is null)
        {
            return Result.Failure<Unit>(UserErrors.NotFound);
        }

        var deactivated = coach.Deactivate(timeProvider.GetUtcNow());
        return deactivated.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(deactivated.Error);
    }
}

internal sealed class GetCoachHandler(IUserRepository users, ICurrentUser currentUser) : IQueryHandler<GetCoachQuery, CoachDto>
{
    public async Task<Result<CoachDto>> Handle(GetCoachQuery query, CancellationToken cancellationToken) =>
        await users.FindCoachDtoAsync(currentUser.RequireTenant(), UserId.From(query.Id), cancellationToken) is { } coach
            ? Result.Success(coach)
            : Result.Failure<CoachDto>(UserErrors.NotFound);
}

internal sealed class ListCoachesHandler(IUserRepository users, ICurrentUser currentUser)
    : IQueryHandler<ListCoachesQuery, PagedResult<CoachDto>>
{
    public async Task<Result<PagedResult<CoachDto>>> Handle(ListCoachesQuery query, CancellationToken cancellationToken) =>
        Result.Success(await users.ListCoachesAsync(currentUser.RequireTenant(), query.Page, cancellationToken));
}
