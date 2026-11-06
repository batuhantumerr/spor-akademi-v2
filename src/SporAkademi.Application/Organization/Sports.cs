using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Organization;

public sealed record CreateSportCommand(string Name) : ICommand<SportId>;

public sealed record RenameSportCommand(SportId Id, string Name, uint Version) : ICommand<Unit>;

public sealed record ArchiveSportCommand(SportId Id) : ICommand<Unit>;

public sealed record DeleteSportCommand(SportId Id) : ICommand<Unit>;

public sealed record GetSportQuery(SportId Id) : IQuery<SportDto>;

public sealed record ListSportsQuery(bool IncludeArchived, PageRequest Page) : IQuery<PagedResult<SportDto>>;

internal sealed class SportNameValidator : AbstractValidator<CreateSportCommand>
{
    public SportNameValidator() =>
        RuleFor(c => c.Name).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Sport.NameMaxLength).WithErrorCode("Field.TooLong");
}

internal sealed class RenameSportValidator : AbstractValidator<RenameSportCommand>
{
    public RenameSportValidator() =>
        RuleFor(c => c.Name).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Sport.NameMaxLength).WithErrorCode("Field.TooLong");
}

internal sealed class ListSportsValidator : AbstractValidator<ListSportsQuery>
{
    public ListSportsValidator() => RuleFor(q => q.Page).SetValidator(new PageRequestValidator());
}

internal sealed class CreateSportHandler(ISportRepository sports, ICurrentUser currentUser)
    : ICommandHandler<CreateSportCommand, SportId>
{
    public async Task<Result<SportId>> Handle(CreateSportCommand command, CancellationToken cancellationToken)
    {
        var created = Sport.Create(currentUser.RequireTenant(), command.Name);
        if (created.IsFailure)
        {
            return Result.Failure<SportId>(created.Error);
        }

        if (await sports.NameKeyExistsAsync(created.Value.NameKey, null, cancellationToken))
        {
            return Result.Failure<SportId>(SportErrors.NameTaken);
        }

        sports.Add(created.Value);
        return Result.Success(created.Value.Id);
    }
}

internal sealed class RenameSportHandler(ISportRepository sports) : ICommandHandler<RenameSportCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RenameSportCommand command, CancellationToken cancellationToken)
    {
        var sport = await sports.GetAsync(command.Id, cancellationToken);
        if (sport is null)
        {
            return Result.Failure<Unit>(SportErrors.NotFound);
        }

        // Check uniqueness before changing the aggregate.
        if (await sports.NameKeyExistsAsync(TextNormalization.CaseKey(command.Name), sport.Id, cancellationToken))
        {
            return Result.Failure<Unit>(SportErrors.NameTaken);
        }

        sports.ExpectVersion(sport, command.Version);
        var renamed = sport.Rename(command.Name);
        return renamed.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(renamed.Error);
    }
}

internal sealed class ArchiveSportHandler(ISportRepository sports) : ICommandHandler<ArchiveSportCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ArchiveSportCommand command, CancellationToken cancellationToken)
    {
        var sport = await sports.GetAsync(command.Id, cancellationToken);
        if (sport is null)
        {
            return Result.Failure<Unit>(SportErrors.NotFound);
        }

        sport.Archive();
        return Result.Success(Unit.Value);
    }
}

internal sealed class DeleteSportHandler(ISportRepository sports, IGroupUsage groups) : ICommandHandler<DeleteSportCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteSportCommand command, CancellationToken cancellationToken)
    {
        var sport = await sports.GetAsync(command.Id, cancellationToken);
        if (sport is null)
        {
            return Result.Failure<Unit>(SportErrors.NotFound);
        }

        if (await groups.AnyGroupUsesSportAsync(sport.Id, cancellationToken))
        {
            return Result.Failure<Unit>(SportErrors.InUse);
        }

        sports.Remove(sport);
        return Result.Success(Unit.Value);
    }
}

internal sealed class GetSportHandler(ISportRepository sports) : IQueryHandler<GetSportQuery, SportDto>
{
    public async Task<Result<SportDto>> Handle(GetSportQuery query, CancellationToken cancellationToken) =>
        await sports.FindDtoAsync(query.Id, cancellationToken) is { } dto
            ? Result.Success(dto)
            : Result.Failure<SportDto>(SportErrors.NotFound);
}

internal sealed class ListSportsHandler(ISportRepository sports) : IQueryHandler<ListSportsQuery, PagedResult<SportDto>>
{
    public async Task<Result<PagedResult<SportDto>>> Handle(ListSportsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await sports.ListAsync(query.IncludeArchived, query.Page, cancellationToken));
}
