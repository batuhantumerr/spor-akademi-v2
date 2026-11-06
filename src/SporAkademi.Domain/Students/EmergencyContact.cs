using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Students;

/// <summary>
/// Who to call in an emergency (DEC-044): kept on the student, independent of guardians, so the coach
/// view never touches the guardian table. Name, phone and relationship are all required.
/// </summary>
public sealed record EmergencyContact
{
    public const int NameMaxLength = 100;
    public const int RelationshipMaxLength = 50;

    private EmergencyContact(string name, PhoneNumber phone, string relationship)
    {
        Name = name;
        Phone = phone;
        Relationship = relationship;
    }

    // For EF Core materialization only.
    private EmergencyContact()
    {
        Name = null!;
        Phone = null!;
        Relationship = null!;
    }

    public string Name { get; private init; }

    public PhoneNumber Phone { get; private init; }

    /// <summary>Free text, e.g. "Teyze" or "Komşu".</summary>
    public string Relationship { get; private init; }

    public static Result<EmergencyContact> Create(string? name, PhoneNumber? phone, string? relationship)
    {
        var n = TextNormalization.CollapseSpaces(name ?? string.Empty);
        var r = TextNormalization.CollapseSpaces(relationship ?? string.Empty);
        if (n.Length == 0 || r.Length == 0 || phone is null)
        {
            return Result.Failure<EmergencyContact>(StudentErrors.EmergencyContactRequired);
        }

        if (n.Length > NameMaxLength || r.Length > RelationshipMaxLength)
        {
            return Result.Failure<EmergencyContact>(CommonErrors.FieldTooLong);
        }

        return Result.Success(new EmergencyContact(n, phone, r));
    }
}
