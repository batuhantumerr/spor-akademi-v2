using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Organization;

public readonly record struct FacilityId(Guid Value) : IEntityId<FacilityId>
{
    public static FacilityId From(Guid value) => new(value);
}

public enum FacilityType
{
    Field,
    Pool,
    Court,
    Other,
}

/// <summary>Where groups train (US-07). Name unique per school; in-use facilities are archived, not deleted.</summary>
public sealed class Facility : AggregateRoot<FacilityId>, ITenantScoped
{
    public const int NameMaxLength = OrganizationNames.MaxLength;
    public const int MaxCapacity = 10_000;

    private Facility(FacilityId id, SchoolId tenantId)
        : base(id)
    {
        TenantId = tenantId;
        Name = null!;
        NameKey = null!;
        Address = null!;
    }

    // For EF Core materialization only.
    private Facility()
        : this(default, default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public string Name { get; private set; }

    public string NameKey { get; private set; }

    public FacilityType Type { get; private set; }

    public Address Address { get; private set; }

    public int? Capacity { get; private set; }

    public bool IsArchived { get; private set; }

    public static Result<Facility> Create(SchoolId tenantId, string name, FacilityType type, Address address, int? capacity)
    {
        var facility = new Facility(EntityId.New<FacilityId>(), tenantId);
        var updated = facility.Update(name, type, address, capacity);
        return updated.IsSuccess ? Result.Success(facility) : Result.Failure<Facility>(updated.Error);
    }

    public Result Update(string name, FacilityType type, Address address, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(address);

        var validName = OrganizationNames.Validate(name);
        if (validName.IsFailure)
        {
            return Result.Failure(validName.Error);
        }

        if (!Enum.IsDefined(type) || capacity is < 1 or > MaxCapacity)
        {
            return Result.Failure(CommonErrors.FieldInvalid);
        }

        Name = validName.Value;
        NameKey = TextNormalization.CaseKey(Name);
        Type = type;
        Address = address;
        Capacity = capacity;
        return Result.Success();
    }

    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;
}

public static class FacilityErrors
{
    public static readonly Error NameTaken = Error.Conflict("Facility.NameTaken", "A facility with this name exists.");
    public static readonly Error InUse = Error.Conflict("Facility.InUse", "Active groups use the facility.");
    public static readonly Error Archived = Error.BusinessRule("Facility.Archived", "The facility is archived.");
    public static readonly Error NotFound = Error.NotFound("Facility.NotFound", "Facility not found.");
}
