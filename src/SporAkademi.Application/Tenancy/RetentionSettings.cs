using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Tenancy;

/// <summary>
/// The school's retention policy as the School Admin sees it (P2-F5, DEC-361), with the bounds the form enforces.
/// <see cref="PaymentYears"/> is shown but not editable: it is a legal minimum.
/// </summary>
public sealed record RetentionPolicyDto(
    int PersonalDataMonths,
    int PaymentYears,
    int MinPersonalDataMonths,
    int MaxPersonalDataMonths,
    uint Version);

/// <summary>The caller's own school; there is no school id to name, so another school cannot be reached.</summary>
public sealed record GetRetentionPolicyQuery : IQuery<RetentionPolicyDto>;

public sealed record UpdateRetentionPolicyCommand(int PersonalDataMonths, uint Version) : ICommand<Unit>;

internal sealed class UpdateRetentionPolicyValidator : AbstractValidator<UpdateRetentionPolicyCommand>
{
    public UpdateRetentionPolicyValidator()
    {
        RuleFor(c => c.PersonalDataMonths)
            .InclusiveBetween(RetentionPolicy.MinPersonalDataMonths, RetentionPolicy.MaxPersonalDataMonths)
            .WithErrorCode(CommonErrors.FieldInvalid.Code);
        RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode(CommonErrors.FieldRequired.Code);
    }
}

internal sealed class GetRetentionPolicyHandler(ISchoolRepository schools, ICurrentUser currentUser)
    : IQueryHandler<GetRetentionPolicyQuery, RetentionPolicyDto>
{
    public async Task<Result<RetentionPolicyDto>> Handle(GetRetentionPolicyQuery query, CancellationToken cancellationToken) =>
        await schools.FindRetentionPolicyAsync(currentUser.RequireTenant(), cancellationToken) is { } dto
            ? Result.Success(dto)
            : Result.Failure<RetentionPolicyDto>(SchoolErrors.NotFound);
}

internal sealed class UpdateRetentionPolicyHandler(ISchoolRepository schools, ICurrentUser currentUser)
    : ICommandHandler<UpdateRetentionPolicyCommand, Unit>
{
    public async Task<Result<Unit>> Handle(UpdateRetentionPolicyCommand command, CancellationToken cancellationToken)
    {
        var school = await schools.GetAsync(currentUser.RequireTenant(), cancellationToken);
        if (school is null)
        {
            return Result.Failure<Unit>(SchoolErrors.NotFound);
        }

        schools.ExpectVersion(school, command.Version);
        var changed = school.ChangePersonalDataRetention(command.PersonalDataMonths);
        return changed.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(changed.Error);
    }
}
