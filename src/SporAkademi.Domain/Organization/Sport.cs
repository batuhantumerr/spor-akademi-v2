using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Organization;

public readonly record struct SportId(Guid Value) : IEntityId<SportId>
{
    public static SportId From(Guid value) => new(value);
}

/// <summary>A sport a school offers (US-06). The name is unique per school, case-insensitively.</summary>
public sealed class Sport : AggregateRoot<SportId>, ITenantScoped
{
    public const int NameMaxLength = 100;

    private Sport(SportId id, SchoolId tenantId, string name)
        : base(id)
    {
        TenantId = tenantId;
        Name = name;
        NameKey = TextNormalization.CaseKey(name);
    }

    // For EF Core materialization only.
    private Sport()
        : base(default)
    {
        Name = null!;
        NameKey = null!;
    }

    public SchoolId TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Lower-cased (Turkish rules) name backing the per-school unique index.</summary>
    public string NameKey { get; private set; }

    public bool IsArchived { get; private set; }

    public static Result<Sport> Create(SchoolId tenantId, string name)
    {
        var validName = OrganizationNames.Validate(name);
        return validName.IsSuccess
            ? Result.Success(new Sport(EntityId.New<SportId>(), tenantId, validName.Value))
            : Result.Failure<Sport>(validName.Error);
    }

    public Result Rename(string name)
    {
        var validName = OrganizationNames.Validate(name);
        if (validName.IsFailure)
        {
            return Result.Failure(validName.Error);
        }

        Name = validName.Value;
        NameKey = TextNormalization.CaseKey(Name);
        return Result.Success();
    }

    /// <summary>Archived sports stay for history but are not offered for new groups.</summary>
    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;
}

public static class SportErrors
{
    public static readonly Error NameTaken = Error.Conflict("Sport.NameTaken", "A sport with this name exists.");
    public static readonly Error InUse = Error.Conflict("Sport.InUse", "The sport has groups.");
    public static readonly Error Archived = Error.BusinessRule("Sport.Archived", "The sport is archived.");
    public static readonly Error NotFound = Error.NotFound("Sport.NotFound", "Sport not found.");
}

internal static class OrganizationNames
{
    public const int MaxLength = 100;

    public static Result<string> Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = TextNormalization.CollapseSpaces(name);
        return trimmed.Length switch
        {
            0 => Result.Failure<string>(CommonErrors.FieldRequired),
            > MaxLength => Result.Failure<string>(CommonErrors.FieldTooLong),
            _ => Result.Success(trimmed),
        };
    }
}
