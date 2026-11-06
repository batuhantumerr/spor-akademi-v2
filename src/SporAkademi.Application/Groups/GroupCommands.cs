using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Groups;

/// <summary>The editable fields of a group, shared by create and update.</summary>
public interface IGroupFields
{
    string Name { get; }

    BirthYearRangeDto BirthYearRange { get; }

    int Capacity { get; }

    Guid FacilityId { get; }

    Guid? CoachId { get; }
}

public sealed record CreateGroupCommand(Guid SportId, string Name, BirthYearRangeDto BirthYearRange, int Capacity, Guid FacilityId, Guid? CoachId)
    : ICommand<GroupId>, IGroupFields;

public sealed record UpdateGroupCommand(GroupId Id, string Name, BirthYearRangeDto BirthYearRange, int Capacity, Guid FacilityId, Guid? CoachId, uint Version)
    : ICommand<Unit>, IGroupFields;

public sealed record SlotInput(DayOfWeek Day, string Start, string End);

/// <summary>Replaces the weekly schedule; returns the facility overlap warnings (US-10).</summary>
public sealed record ReplaceScheduleCommand(GroupId Id, IReadOnlyList<SlotInput> Slots, uint Version) : ICommand<IReadOnlyList<Warning>>;

public sealed record ArchiveGroupCommand(GroupId Id) : ICommand<Unit>;

internal abstract class GroupFieldsValidator<T> : AbstractValidator<T>
    where T : IGroupFields
{
    protected GroupFieldsValidator()
    {
        RuleFor(i => i.Name).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Group.NameMaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.BirthYearRange).NotNull().WithErrorCode("Field.Required");
        RuleFor(i => i.BirthYearRange).Must(r => Domain.Groups.BirthYearRange.Create(r.From, r.To).IsSuccess)
            .WithErrorCode(Domain.Groups.BirthYearRange.Invalid.Code).When(i => i.BirthYearRange is not null);
        RuleFor(i => i.Capacity).InclusiveBetween(1, Group.MaxCapacity).WithErrorCode(GroupErrors.InvalidCapacity.Code);
        RuleFor(i => i.FacilityId).NotEmpty().WithErrorCode("Field.Required");
        RuleFor(i => i.CoachId).NotEqual(Guid.Empty).WithErrorCode("Field.Invalid");
    }
}

internal sealed class CreateGroupValidator : GroupFieldsValidator<CreateGroupCommand>
{
    public CreateGroupValidator() => RuleFor(c => c.SportId).NotEmpty().WithErrorCode("Field.Required");
}

internal sealed class UpdateGroupValidator : GroupFieldsValidator<UpdateGroupCommand>
{
    public UpdateGroupValidator() => RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode("Field.Required");
}

internal sealed class ReplaceScheduleValidator : AbstractValidator<ReplaceScheduleCommand>
{
    public ReplaceScheduleValidator()
    {
        RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode("Field.Required");
        RuleFor(c => c.Slots).NotNull().WithErrorCode("Field.Required");
        RuleFor(c => c.Slots.Count).LessThanOrEqualTo(Group.MaxScheduleSlots).WithErrorCode("Field.TooLong")
            .OverridePropertyName("slots").When(c => c.Slots is not null);
        RuleForEach(c => c.Slots).ChildRules(slot =>
        {
            slot.RuleFor(s => s.Day).IsInEnum().WithErrorCode("Field.Invalid");
            slot.RuleFor(s => s.Start).Must(s => TimeFormat.TryParse(s, out _)).WithErrorCode("Field.Invalid");
            slot.RuleFor(s => s.End).Must(s => TimeFormat.TryParse(s, out _)).WithErrorCode("Field.Invalid");
            slot.RuleFor(s => s.End).Must((s, _) => TimeFormat.ToTimeRange(s.Start, s.End).IsSuccess)
                .WithErrorCode(TimeRange.Invalid.Code)
                .When(s => TimeFormat.TryParse(s.Start, out _) && TimeFormat.TryParse(s.End, out _));
        }).When(c => c.Slots is not null);
    }
}

/// <summary>Cross-module checks shared by create and update (US-09): sport, facility and coach must be usable.</summary>
internal static class GroupReferences
{
    public static async Task<Error?> CheckSportAsync(IOrganizationDirectory organization, SportId sportId, CancellationToken cancellationToken) =>
        await organization.FindSportAsync(sportId, cancellationToken) switch
        {
            null => SportErrors.NotFound,
            { IsArchived: true } => SportErrors.Archived,
            _ => null,
        };

    public static async Task<Error?> CheckFacilityAsync(IOrganizationDirectory organization, FacilityId facilityId, CancellationToken cancellationToken) =>
        await organization.FindFacilityAsync(facilityId, cancellationToken) switch
        {
            null => FacilityErrors.NotFound,
            { IsArchived: true } => FacilityErrors.Archived,
            _ => null,
        };

    public static async Task<Error?> CheckCoachAsync(ICoachDirectory coaches, Guid? coachId, CancellationToken cancellationToken) =>
        coachId is { } id && await coaches.FindAsync(id, cancellationToken) is not { IsActive: true }
            ? GroupErrors.CoachNotFound
            : null;

    public static Result<BirthYearRange> ToBirthYearRange(this IGroupFields input)
    {
        var range = BirthYearRange.Create(input.BirthYearRange.From, input.BirthYearRange.To);
        return range.IsSuccess ? range : Result.Failure<BirthYearRange>(ValidationErrors.ForField("birthYearRange", range.Error));
    }

    /// <summary>Domain validation failures (backstops of the validator) in the field-error shape.</summary>
    public static Error ToFieldError(Error error) =>
        error == GroupErrors.InvalidCapacity ? ValidationErrors.ForField("capacity", error)
        : error.Type == ErrorType.Validation ? ValidationErrors.ForField("name", error)
        : error;

    public static UserId? ToCoachId(this IGroupFields input) => input.CoachId is { } id ? UserId.From(id) : null;
}

internal sealed class CreateGroupHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ICoachDirectory coaches,
    ICurrentUser currentUser) : ICommandHandler<CreateGroupCommand, GroupId>
{
    public async Task<Result<GroupId>> Handle(CreateGroupCommand command, CancellationToken cancellationToken)
    {
        var refused = await GroupReferences.CheckSportAsync(organization, SportId.From(command.SportId), cancellationToken)
            ?? await GroupReferences.CheckFacilityAsync(organization, FacilityId.From(command.FacilityId), cancellationToken)
            ?? await GroupReferences.CheckCoachAsync(coaches, command.CoachId, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure<GroupId>(refused);
        }

        var range = command.ToBirthYearRange();
        if (range.IsFailure)
        {
            return Result.Failure<GroupId>(range.Error);
        }

        var created = Group.Create(
            currentUser.RequireTenant(),
            SportId.From(command.SportId),
            FacilityId.From(command.FacilityId),
            command.ToCoachId(),
            command.Name,
            range.Value,
            command.Capacity);
        if (created.IsFailure)
        {
            return Result.Failure<GroupId>(GroupReferences.ToFieldError(created.Error));
        }

        if (await groups.NameKeyExistsAsync(created.Value.NameKey, null, cancellationToken))
        {
            return Result.Failure<GroupId>(GroupErrors.NameTaken);
        }

        groups.Add(created.Value);
        return Result.Success(created.Value.Id);
    }
}

internal sealed class UpdateGroupHandler(
    IGroupRepository groups,
    IOrganizationDirectory organization,
    ICoachDirectory coaches,
    IEnrollmentCounts enrollments) : ICommandHandler<UpdateGroupCommand, Unit>
{
    public async Task<Result<Unit>> Handle(UpdateGroupCommand command, CancellationToken cancellationToken)
    {
        var group = await groups.GetAsync(command.Id, cancellationToken);
        if (group is null)
        {
            return Result.Failure<Unit>(GroupErrors.NotFound);
        }

        if (group.IsArchived)
        {
            return Result.Failure<Unit>(GroupErrors.Archived);
        }

        // Only a newly chosen facility or coach must be usable; an unchanged one stays valid
        // (an archived facility keeps its groups, a deactivated coach is cleared by an event).
        var facilityId = FacilityId.From(command.FacilityId);
        var coachId = command.ToCoachId();
        var facilityChanged = facilityId != group.FacilityId;
        var coachChanged = coachId != group.CoachId;
        var refused = (facilityChanged ? await GroupReferences.CheckFacilityAsync(organization, facilityId, cancellationToken) : null)
            ?? (coachChanged ? await GroupReferences.CheckCoachAsync(coaches, command.CoachId, cancellationToken) : null);
        if (refused is not null)
        {
            return Result.Failure<Unit>(refused);
        }

        if (command.Capacity < group.Capacity)
        {
            var active = await enrollments.CountActiveAsync(group.Id, cancellationToken);
            if (command.Capacity < active)
            {
                return Result.Failure<Unit>(GroupErrors.CapacityBelowActiveEnrollments(active));
            }
        }

        if (await groups.NameKeyExistsAsync(TextNormalization.CaseKey(command.Name), group.Id, cancellationToken))
        {
            return Result.Failure<Unit>(GroupErrors.NameTaken);
        }

        var range = command.ToBirthYearRange();
        if (range.IsFailure)
        {
            return Result.Failure<Unit>(range.Error);
        }

        groups.ExpectVersion(group, command.Version);
        var updated = group.Update(command.Name, range.Value, command.Capacity, facilityId, coachId);
        if (updated.IsFailure)
        {
            return Result.Failure<Unit>(GroupReferences.ToFieldError(updated.Error));
        }

        // A new coach must be free at the group's times (US-10); facility overlaps are only reported by
        // the schedule endpoint, so warnings are not returned here.
        if (coachChanged || facilityChanged)
        {
            var others = await groups.ListActiveWeeksAsync(group.Id, group.CoachId, group.FacilityId, cancellationToken);
            var conflicts = ScheduleConflictChecker.Check(GroupWeek.Of(group), others);
            if (conflicts.IsFailure)
            {
                return Result.Failure<Unit>(conflicts.Error);
            }
        }

        return Result.Success(Unit.Value);
    }
}

internal sealed class ReplaceScheduleHandler(IGroupRepository groups) : ICommandHandler<ReplaceScheduleCommand, IReadOnlyList<Warning>>
{
    public async Task<Result<IReadOnlyList<Warning>>> Handle(ReplaceScheduleCommand command, CancellationToken cancellationToken)
    {
        var group = await groups.GetAsync(command.Id, cancellationToken);
        if (group is null)
        {
            return Result.Failure<IReadOnlyList<Warning>>(GroupErrors.NotFound);
        }

        var slots = new List<ScheduleSlot>(command.Slots.Count);
        for (var i = 0; i < command.Slots.Count; i++)
        {
            var input = command.Slots[i];
            var time = TimeFormat.ToTimeRange(input.Start, input.End);
            var slot = time.IsSuccess ? ScheduleSlot.Create(input.Day, time.Value) : Result.Failure<ScheduleSlot>(time.Error);
            if (slot.IsFailure)
            {
                return Result.Failure<IReadOnlyList<Warning>>(ValidationErrors.ForField($"slots[{i}]", slot.Error));
            }

            slots.Add(slot.Value);
        }

        groups.ExpectVersion(group, command.Version);
        var replaced = group.ReplaceSchedule(slots);
        if (replaced.IsFailure)
        {
            return Result.Failure<IReadOnlyList<Warning>>(replaced.Error);
        }

        var others = await groups.ListActiveWeeksAsync(group.Id, group.CoachId, group.FacilityId, cancellationToken);
        return ScheduleConflictChecker.Check(GroupWeek.Of(group), others);
    }
}

internal sealed class ArchiveGroupHandler(IGroupRepository groups, ITenancyModule tenancy, ICurrentUser currentUser)
    : ICommandHandler<ArchiveGroupCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ArchiveGroupCommand command, CancellationToken cancellationToken)
    {
        var group = await groups.GetAsync(command.Id, cancellationToken);
        if (group is null)
        {
            return Result.Failure<Unit>(GroupErrors.NotFound);
        }

        var today = await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
        var archived = group.Archive(today);
        return archived.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(archived.Error);
    }
}

/// <summary>US-11: archiving a group ends its active enrollments with the archive date, in the same transaction.</summary>
internal sealed class EndEnrollmentsWhenGroupArchived(IEnrollmentEnder enrollments) : IDomainEventConsumer<GroupArchived>
{
    public Task<Result> Handle(GroupArchived domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return enrollments.EndActiveEnrollmentsAsync(domainEvent.GroupId, domainEvent.On, cancellationToken);
    }
}

/// <summary>
/// US-08 / appendix B §6: a deactivated coach's groups stay but have no coach until reassigned. Runs in the
/// deactivating School Admin's request, so the tenant filter limits it to that school.
/// </summary>
internal sealed class ClearCoachWhenUserDeactivated(IGroupRepository groups) : IDomainEventConsumer<UserDeactivated>
{
    public async Task<Result> Handle(UserDeactivated domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.TenantId is null)
        {
            // The Platform Admin coaches no group.
            return Result.Success();
        }

        foreach (var group in await groups.ListByCoachAsync(domainEvent.UserId, cancellationToken))
        {
            group.ClearCoach();
        }

        return Result.Success();
    }
}
