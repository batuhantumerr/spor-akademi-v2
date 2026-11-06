using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Students;

public readonly record struct GuardianId(Guid Value) : IEntityId<GuardianId>
{
    public static GuardianId From(Guid value) => new(value);
}

/// <summary>
/// A parent or legal guardian of one or more minor students (appendix B §4.7). The phone number is
/// unique per school, which is how siblings share the same guardian (US-13).
/// </summary>
public sealed class Guardian : AggregateRoot<GuardianId>, ITenantScoped
{
    private Guardian(GuardianId id, SchoolId tenantId)
        : base(id)
    {
        TenantId = tenantId;
        FirstName = null!;
        LastName = null!;
        Phone = null!;
    }

    // For EF Core materialization only.
    private Guardian()
        : this(default, default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public PhoneNumber Phone { get; private set; }

    public Email? Email { get; private set; }

    public Address? Address { get; private set; }

    public static Result<Guardian> Create(SchoolId tenantId, string firstName, string lastName, PhoneNumber phone, Email? email, Address? address)
    {
        var guardian = new Guardian(EntityId.New<GuardianId>(), tenantId);
        var updated = guardian.Update(firstName, lastName, phone, email, address);
        return updated.IsSuccess ? Result.Success(guardian) : Result.Failure<Guardian>(updated.Error);
    }

    public Result Update(string firstName, string lastName, PhoneNumber phone, Email? email, Address? address)
    {
        ArgumentNullException.ThrowIfNull(phone);

        var names = PersonNames.Validate(firstName, lastName);
        if (names.IsFailure)
        {
            return Result.Failure(names.Error);
        }

        (FirstName, LastName) = names.Value;
        Phone = phone;
        Email = email;
        Address = address;
        return Result.Success();
    }
}

public static class GuardianErrors
{
    public static readonly Error NotFound = Error.NotFound("Guardian.NotFound", "Guardian not found.");
    public static readonly Error PhoneTaken = Error.Conflict("Guardian.PhoneTaken", "Another guardian has this phone number.");
}
