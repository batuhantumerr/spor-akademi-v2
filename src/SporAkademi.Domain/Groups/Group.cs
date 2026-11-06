using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Groups;

public readonly record struct GroupId(Guid Value) : IEntityId<GroupId>
{
    public static GroupId From(Guid value) => new(value);
}

public enum GroupStatus
{
    Active,
    Archived,
}

/// <summary>The group was archived on <paramref name="On"/> (the school's today); its active enrollments end that day (US-11).</summary>
public sealed record GroupArchived(GroupId GroupId, SchoolId TenantId, DateOnly On) : IDomainEvent;

/// <summary>
/// Students of one sport and birth-year range training together on a weekly schedule (US-09, US-10, US-11).
/// Rules that need other aggregates (coach availability, facility overlap, capacity against enrollments)
/// are checked outside: <see cref="ScheduleConflictChecker"/> and the application layer.
/// </summary>
public sealed class Group : AggregateRoot<GroupId>, ITenantScoped
{
    public const int NameMaxLength = 100;
    public const int MaxCapacity = 1_000;
    public const int MaxScheduleSlots = 21;

    /// <summary>Name of the field holding the schedule, for the persistence mapping.</summary>
    public const string ScheduleSlotsField = nameof(_scheduleSlots);

    private List<ScheduleSlot> _scheduleSlots = [];

    // Fees (appendix B 4.5, P1-31) will be a second owned collection next to the schedule, changed only
    // through methods of this aggregate.
    private Group(GroupId id, SchoolId tenantId, SportId sportId)
        : base(id)
    {
        TenantId = tenantId;
        SportId = sportId;
        Name = null!;
        NameKey = null!;
        BirthYearRange = null!;
        Status = GroupStatus.Active;
    }

    // For EF Core materialization only.
    private Group()
        : this(default, default, default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public SportId SportId { get; private set; }

    public FacilityId FacilityId { get; private set; }

    /// <summary>Null when no coach is assigned, e.g. after the coach was deactivated (US-08).</summary>
    public UserId? CoachId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Lower-cased (Turkish rules) name backing the per-school unique index.</summary>
    public string NameKey { get; private set; }

    public BirthYearRange BirthYearRange { get; private set; }

    public int Capacity { get; private set; }

    public GroupStatus Status { get; private set; }

    /// <summary>The school's date on which the group was archived; planned sessions stop after it.</summary>
    public DateOnly? ArchivedOn { get; private set; }

    public IReadOnlyList<ScheduleSlot> ScheduleSlots => _scheduleSlots.AsReadOnly();

    public bool IsArchived => Status == GroupStatus.Archived;

    public static Result<Group> Create(
        SchoolId tenantId,
        SportId sportId,
        FacilityId facilityId,
        UserId? coachId,
        string name,
        BirthYearRange birthYearRange,
        int capacity)
    {
        var group = new Group(EntityId.New<GroupId>(), tenantId, sportId);
        var set = group.SetDetails(name, birthYearRange, capacity, facilityId, coachId);
        return set.IsSuccess ? Result.Success(group) : Result.Failure<Group>(set.Error);
    }

    /// <summary>Changes the editable fields (US-11). The sport is fixed: a group of another sport is another group.</summary>
    public Result Update(string name, BirthYearRange birthYearRange, int capacity, FacilityId facilityId, UserId? coachId) =>
        IsArchived ? Result.Failure(GroupErrors.Archived) : SetDetails(name, birthYearRange, capacity, facilityId, coachId);

    /// <summary>Replaces the whole weekly schedule (US-10). Slots of one group never overlap.</summary>
    public Result ReplaceSchedule(IReadOnlyCollection<ScheduleSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (IsArchived)
        {
            return Result.Failure(GroupErrors.Archived);
        }

        if (slots.Count > MaxScheduleSlots)
        {
            return Result.Failure(CommonErrors.FieldInvalid);
        }

        var ordered = slots.OrderBy(s => s.Day).ThenBy(s => s.Time.Start).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (ordered[i].Overlaps(ordered[j]))
                {
                    return Result.Failure(ScheduleErrors.SlotOverlap);
                }
            }
        }

        _scheduleSlots = ordered;
        return Result.Success();
    }

    /// <summary>Archives the group on the school's <paramref name="today"/>; its active enrollments end that day.</summary>
    public Result Archive(DateOnly today)
    {
        if (IsArchived)
        {
            return Result.Failure(GroupErrors.Archived);
        }

        Status = GroupStatus.Archived;
        ArchivedOn = today;
        Raise(new GroupArchived(Id, TenantId, today));
        return Result.Success();
    }

    /// <summary>The coach left (deactivated): the group stays, shown as "no active coach" until reassigned (US-08).</summary>
    public void ClearCoach() => CoachId = null;

    private Result SetDetails(string name, BirthYearRange birthYearRange, int capacity, FacilityId facilityId, UserId? coachId)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(birthYearRange);

        var trimmed = TextNormalization.CollapseSpaces(name);
        if (trimmed.Length == 0)
        {
            return Result.Failure(CommonErrors.FieldRequired);
        }

        if (trimmed.Length > NameMaxLength)
        {
            return Result.Failure(CommonErrors.FieldTooLong);
        }

        if (capacity is < 1 or > MaxCapacity)
        {
            return Result.Failure(GroupErrors.InvalidCapacity);
        }

        Name = trimmed;
        NameKey = TextNormalization.CaseKey(trimmed);
        BirthYearRange = birthYearRange;
        Capacity = capacity;
        FacilityId = facilityId;
        CoachId = coachId;
        return Result.Success();
    }
}

public static class GroupErrors
{
    public static readonly Error NotFound = Error.NotFound("Group.NotFound", "Group not found.");
    public static readonly Error NameTaken = Error.Conflict("Group.NameTaken", "A group with this name exists.");
    public static readonly Error InvalidCapacity = Error.Validation("Group.InvalidCapacity", "Capacity must be between 1 and 1000.");
    public static readonly Error Archived = Error.BusinessRule("Group.Archived", "The group is archived.");
    public static readonly Error CoachNotFound = Error.BusinessRule("Group.CoachNotFound", "The coach does not exist or is not active.");

    /// <summary>Argument <c>count</c>: the number of active enrollments.</summary>
    public static Error CapacityBelowActiveEnrollments(int count) =>
        Error.BusinessRule("Group.CapacityBelowActiveEnrollments", "Capacity is below the active enrollments.").WithArgument("count", count);
}

public static class ScheduleErrors
{
    public static readonly Error SlotOverlap = Error.BusinessRule("Schedule.SlotOverlap", "Slots of the group overlap.");

    /// <summary>Argument <c>groupName</c>: the coach's other group at that time.</summary>
    public static Error CoachUnavailable(string groupName) =>
        Error.BusinessRule("Schedule.CoachUnavailable", "The coach has another group at this time.").WithArgument("groupName", groupName);
}
