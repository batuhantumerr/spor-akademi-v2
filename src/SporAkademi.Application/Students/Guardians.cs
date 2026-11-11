using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students;

public sealed record UpdateGuardianCommand(
    GuardianId Id,
    string FirstName,
    string LastName,
    string Phone,
    string? Email,
    GuardianAddressDto? Address,
    uint Version) : ICommand<Unit>, IGuardianFields;

public sealed record GetGuardianQuery(GuardianId Id) : IQuery<GuardianDto>;

/// <summary>Finds the school's guardian with this phone, e.g. to link a sibling's guardian (US-13). At most one item.</summary>
public sealed record FindGuardiansByPhoneQuery(string? Phone) : IQuery<PagedResult<GuardianDto>>;

internal sealed class UpdateGuardianValidator : GuardianFieldsValidator<UpdateGuardianCommand>
{
    public UpdateGuardianValidator() => RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode("Field.Required");
}

internal sealed class FindGuardiansByPhoneValidator : AbstractValidator<FindGuardiansByPhoneQuery>
{
    public FindGuardiansByPhoneValidator() =>
        RuleFor(q => q.Phone).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(StudentInputLimits.RawPhoneMaxLength).WithErrorCode("Field.TooLong");
}

internal sealed class UpdateGuardianHandler(IGuardianRepository guardians, IIdentityModule identity) : ICommandHandler<UpdateGuardianCommand, Unit>
{
    public async Task<Result<Unit>> Handle(UpdateGuardianCommand command, CancellationToken cancellationToken)
    {
        var guardian = await guardians.GetAsync(command.Id, cancellationToken);
        if (guardian is null)
        {
            return Result.Failure<Unit>(GuardianErrors.NotFound);
        }

        var values = GuardianValues.From(command, string.Empty);
        if (values.IsFailure)
        {
            return Result.Failure<Unit>(values.Error);
        }

        if (await guardians.PhoneExistsAsync(values.Value.Phone, guardian.Id, cancellationToken))
        {
            return Result.Failure<Unit>(GuardianErrors.PhoneTaken);
        }

        guardians.ExpectVersion(guardian, command.Version);
        var previousEmail = guardian.Email;
        var updated = values.Value.UpdateOf(guardian);
        if (updated.IsFailure)
        {
            return Result.Failure<Unit>(updated.Error);
        }

        // DEC-379: the account (and a pending link) belongs to the address it was invited with. A changed or cleared
        // address closes it, so a link sent to a mistyped address stops working; the admin invites the new address.
        if (guardian.AccountUserId is { } account && guardian.Email != previousEmail)
        {
            await identity.CloseParentAccountAsync(guardian.TenantId, account, erasePersonalData: false, cancellationToken);
        }

        return Result.Success(Unit.Value);
    }
}

internal sealed class GetGuardianHandler(IGuardianRepository guardians) : IQueryHandler<GetGuardianQuery, GuardianDto>
{
    public async Task<Result<GuardianDto>> Handle(GetGuardianQuery query, CancellationToken cancellationToken) =>
        await guardians.FindDtoAsync(query.Id, cancellationToken) is { } dto
            ? Result.Success(dto)
            : Result.Failure<GuardianDto>(GuardianErrors.NotFound);
}

internal sealed class FindGuardiansByPhoneHandler(IGuardianRepository guardians)
    : IQueryHandler<FindGuardiansByPhoneQuery, PagedResult<GuardianDto>>
{
    public async Task<Result<PagedResult<GuardianDto>>> Handle(FindGuardiansByPhoneQuery query, CancellationToken cancellationToken)
    {
        var phone = PhoneNumber.Create(query.Phone!);
        if (phone.IsFailure)
        {
            return Result.Failure<PagedResult<GuardianDto>>(ValidationErrors.ForField("phone", phone.Error));
        }

        GuardianDto[] items = await guardians.FindDtoByPhoneAsync(phone.Value, cancellationToken) is { } dto ? [dto] : [];
        return Result.Success(new PagedResult<GuardianDto>(items, 1, PageRequest.DefaultPageSize, items.Length));
    }
}
