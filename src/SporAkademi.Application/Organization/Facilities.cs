using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Organization;

/// <summary>The editable fields of a facility, shared by create and update.</summary>
public interface IFacilityFields
{
    string Name { get; }

    FacilityType Type { get; }

    AddressDto Address { get; }

    int? Capacity { get; }
}

public sealed record CreateFacilityCommand(string Name, FacilityType Type, AddressDto Address, int? Capacity)
    : ICommand<FacilityId>, IFacilityFields;

public sealed record UpdateFacilityCommand(FacilityId Id, string Name, FacilityType Type, AddressDto Address, int? Capacity, uint Version)
    : ICommand<Unit>, IFacilityFields;

public sealed record ArchiveFacilityCommand(FacilityId Id) : ICommand<Unit>;

public sealed record DeleteFacilityCommand(FacilityId Id) : ICommand<Unit>;

public sealed record GetFacilityQuery(FacilityId Id) : IQuery<FacilityDto>;

public sealed record ListFacilitiesQuery(bool IncludeArchived, PageRequest Page) : IQuery<PagedResult<FacilityDto>>;

internal abstract class FacilityFieldsValidator<T> : AbstractValidator<T>
    where T : IFacilityFields
{
    protected FacilityFieldsValidator()
    {
        RuleFor(i => i.Name).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Facility.NameMaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.Type).IsInEnum().WithErrorCode("Field.Invalid");
        RuleFor(i => i.Capacity).InclusiveBetween(1, Facility.MaxCapacity).When(i => i.Capacity is not null).WithErrorCode("Field.Invalid");
        RuleFor(i => i.Address).NotNull().WithErrorCode("Field.Required");
        RuleFor(i => i.Address.Line).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Address.LineMaxLength).WithErrorCode("Field.TooLong").When(i => i.Address is not null);
        RuleFor(i => i.Address.District).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Address.PartMaxLength).WithErrorCode("Field.TooLong").When(i => i.Address is not null);
        RuleFor(i => i.Address.City).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Address.PartMaxLength).WithErrorCode("Field.TooLong").When(i => i.Address is not null);
    }
}

internal sealed class CreateFacilityValidator : FacilityFieldsValidator<CreateFacilityCommand>;

internal sealed class UpdateFacilityValidator : FacilityFieldsValidator<UpdateFacilityCommand>
{
    public UpdateFacilityValidator() => RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode("Field.Required");
}

internal sealed class ListFacilitiesValidator : AbstractValidator<ListFacilitiesQuery>
{
    public ListFacilitiesValidator()
    {
        // Paging errors use the query-string names (page, pageSize).
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
    }
}

internal static class FacilityInputMapping
{
    public static Result<Address> ToAddress(this IFacilityFields input)
    {
        var address = Address.Create(input.Address.Line, input.Address.District, input.Address.City);
        return address.IsSuccess
            ? address
            : Result.Failure<Address>(ValidationErrors.ForField("address", address.Error));
    }
}

internal sealed class CreateFacilityHandler(IFacilityRepository facilities, ICurrentUser currentUser)
    : ICommandHandler<CreateFacilityCommand, FacilityId>
{
    public async Task<Result<FacilityId>> Handle(CreateFacilityCommand command, CancellationToken cancellationToken)
    {
        var input = command;
        var address = input.ToAddress();
        if (address.IsFailure)
        {
            return Result.Failure<FacilityId>(address.Error);
        }

        var created = Facility.Create(currentUser.RequireTenant(), input.Name, input.Type, address.Value, input.Capacity);
        if (created.IsFailure)
        {
            return Result.Failure<FacilityId>(created.Error);
        }

        if (await facilities.NameKeyExistsAsync(created.Value.NameKey, null, cancellationToken))
        {
            return Result.Failure<FacilityId>(FacilityErrors.NameTaken);
        }

        facilities.Add(created.Value);
        return Result.Success(created.Value.Id);
    }
}

internal sealed class UpdateFacilityHandler(IFacilityRepository facilities) : ICommandHandler<UpdateFacilityCommand, Unit>
{
    public async Task<Result<Unit>> Handle(UpdateFacilityCommand command, CancellationToken cancellationToken)
    {
        var facility = await facilities.GetAsync(command.Id, cancellationToken);
        if (facility is null)
        {
            return Result.Failure<Unit>(FacilityErrors.NotFound);
        }

        var input = command;
        if (await facilities.NameKeyExistsAsync(TextNormalization.CaseKey(input.Name), facility.Id, cancellationToken))
        {
            return Result.Failure<Unit>(FacilityErrors.NameTaken);
        }

        var address = input.ToAddress();
        if (address.IsFailure)
        {
            return Result.Failure<Unit>(address.Error);
        }

        facilities.ExpectVersion(facility, command.Version);
        var updated = facility.Update(input.Name, input.Type, address.Value, input.Capacity);
        return updated.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(updated.Error);
    }
}

internal sealed class ArchiveFacilityHandler(IFacilityRepository facilities) : ICommandHandler<ArchiveFacilityCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ArchiveFacilityCommand command, CancellationToken cancellationToken)
    {
        var facility = await facilities.GetAsync(command.Id, cancellationToken);
        if (facility is null)
        {
            return Result.Failure<Unit>(FacilityErrors.NotFound);
        }

        facility.Archive();
        return Result.Success(Unit.Value);
    }
}

internal sealed class DeleteFacilityHandler(IFacilityRepository facilities, IGroupUsage groups)
    : ICommandHandler<DeleteFacilityCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteFacilityCommand command, CancellationToken cancellationToken)
    {
        var facility = await facilities.GetAsync(command.Id, cancellationToken);
        if (facility is null)
        {
            return Result.Failure<Unit>(FacilityErrors.NotFound);
        }

        if (await groups.AnyActiveGroupUsesFacilityAsync(facility.Id, cancellationToken))
        {
            return Result.Failure<Unit>(FacilityErrors.InUse);
        }

        facilities.Remove(facility);
        return Result.Success(Unit.Value);
    }
}

internal sealed class GetFacilityHandler(IFacilityRepository facilities) : IQueryHandler<GetFacilityQuery, FacilityDto>
{
    public async Task<Result<FacilityDto>> Handle(GetFacilityQuery query, CancellationToken cancellationToken) =>
        await facilities.FindDtoAsync(query.Id, cancellationToken) is { } dto
            ? Result.Success(dto)
            : Result.Failure<FacilityDto>(FacilityErrors.NotFound);
}

internal sealed class ListFacilitiesHandler(IFacilityRepository facilities)
    : IQueryHandler<ListFacilitiesQuery, PagedResult<FacilityDto>>
{
    public async Task<Result<PagedResult<FacilityDto>>> Handle(ListFacilitiesQuery query, CancellationToken cancellationToken) =>
        Result.Success(await facilities.ListAsync(query.IncludeArchived, query.Page, cancellationToken));
}
