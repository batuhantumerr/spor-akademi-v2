using FluentValidation;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Students;

public sealed record EmergencyContactInput(string? Name, string? Phone, string? Relationship);

public sealed record NewGuardianInput(string FirstName, string LastName, string Phone, string? Email, GuardianAddressDto? Address) : IGuardianFields;

/// <summary>Either an existing guardian of the school (<see cref="GuardianId"/>) or a new one (<see cref="NewGuardian"/>), never both.</summary>
public sealed record GuardianLinkInput(Guid? GuardianId, NewGuardianInput? NewGuardian, GuardianRelationship Relationship);

/// <summary>The editable fields of a student, shared by register and update.</summary>
public interface IStudentFields
{
    string FirstName { get; }

    string LastName { get; }

    DateOnly BirthDate { get; }

    Gender Gender { get; }

    string? Phone { get; }

    string? Email { get; }

    EmergencyContactInput? EmergencyContact { get; }
}

/// <summary>The guardian fields shared by a new guardian in a registration and a guardian update.</summary>
public interface IGuardianFields
{
    string FirstName { get; }

    string LastName { get; }

    string Phone { get; }

    string? Email { get; }

    GuardianAddressDto? Address { get; }
}

internal static class StudentInputLimits
{
    /// <summary>Raw input bound before normalization (spaces, dashes, parentheses are allowed).</summary>
    public const int RawPhoneMaxLength = 32;
}

internal abstract class StudentFieldsValidator<T> : AbstractValidator<T>
    where T : IStudentFields
{
    protected StudentFieldsValidator()
    {
        RuleFor(i => i.FirstName).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(PersonNames.MaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.LastName).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(PersonNames.MaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.BirthDate).NotEqual(default(DateOnly)).WithErrorCode("Field.Required");
        RuleFor(i => i.Gender).IsInEnum().WithErrorCode("Field.Invalid");
        RuleFor(i => i.Phone).MaximumLength(StudentInputLimits.RawPhoneMaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.Email).MaximumLength(Email.MaxLength).WithErrorCode("Field.TooLong");

        // Any missing part of the emergency contact is one error on the whole contact (appendix E).
        RuleFor(i => i.EmergencyContact)
            .Must(c => c is not null && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Phone) && !string.IsNullOrWhiteSpace(c.Relationship))
            .WithErrorCode(StudentErrors.EmergencyContactRequired.Code);
        RuleFor(i => i.EmergencyContact!.Name).MaximumLength(EmergencyContact.NameMaxLength).WithErrorCode("Field.TooLong")
            .When(i => i.EmergencyContact is not null);
        RuleFor(i => i.EmergencyContact!.Phone).MaximumLength(StudentInputLimits.RawPhoneMaxLength).WithErrorCode("Field.TooLong")
            .When(i => i.EmergencyContact is not null);
        RuleFor(i => i.EmergencyContact!.Relationship).MaximumLength(EmergencyContact.RelationshipMaxLength).WithErrorCode("Field.TooLong")
            .When(i => i.EmergencyContact is not null);
    }
}

internal abstract class GuardianFieldsValidator<T> : AbstractValidator<T>
    where T : IGuardianFields
{
    protected GuardianFieldsValidator()
    {
        RuleFor(i => i.FirstName).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(PersonNames.MaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.LastName).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(PersonNames.MaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.Phone).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(StudentInputLimits.RawPhoneMaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.Email).MaximumLength(Email.MaxLength).WithErrorCode("Field.TooLong");
        RuleFor(i => i.Address!.Line).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Address.LineMaxLength).WithErrorCode("Field.TooLong").When(i => i.Address is not null);
        RuleFor(i => i.Address!.District).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Address.PartMaxLength).WithErrorCode("Field.TooLong").When(i => i.Address is not null);
        RuleFor(i => i.Address!.City).NotEmpty().WithErrorCode("Field.Required")
            .MaximumLength(Address.PartMaxLength).WithErrorCode("Field.TooLong").When(i => i.Address is not null);
    }
}

internal sealed class NewGuardianValidator : GuardianFieldsValidator<NewGuardianInput>;

internal sealed class GuardianLinkValidator : AbstractValidator<GuardianLinkInput>
{
    public GuardianLinkValidator()
    {
        RuleFor(l => l.GuardianId).Must((link, id) => (id is null) != (link.NewGuardian is null)).WithErrorCode("Field.Invalid");
        RuleFor(l => l.Relationship).IsInEnum().WithErrorCode("Field.Invalid");
        RuleFor(l => l.NewGuardian!).SetValidator(new NewGuardianValidator()).When(l => l.NewGuardian is not null);
    }
}

/// <summary>Commands that link guardians: at most <see cref="Student.MaxGuardians"/> links, each valid; a missing list means none.</summary>
internal interface IGuardianLinks
{
    IReadOnlyList<GuardianLinkInput>? Guardians { get; }
}

internal static class GuardianLinkRules
{
    public static void AddGuardianLinkRules<T>(this AbstractValidator<T> validator)
        where T : IGuardianLinks
    {
        validator.RuleFor(c => c.Guardians).Must(l => l is null || l.Count <= Student.MaxGuardians).WithErrorCode("Field.Invalid");
        validator.RuleForEach(c => c.Guardians).SetValidator(new GuardianLinkValidator());
    }
}

internal static class StudentInputMapping
{
    public static Result<StudentDetails> ToDetails(this IStudentFields input)
    {
        var phone = OptionalPhone(input.Phone, "phone");
        if (phone.IsFailure)
        {
            return Result.Failure<StudentDetails>(phone.Error);
        }

        var email = OptionalEmail(input.Email, "email");
        if (email.IsFailure)
        {
            return Result.Failure<StudentDetails>(email.Error);
        }

        var contact = input.EmergencyContact;
        if (contact is null || string.IsNullOrWhiteSpace(contact.Phone))
        {
            return Result.Failure<StudentDetails>(ValidationErrors.ForField("emergencyContact", StudentErrors.EmergencyContactRequired));
        }

        var contactPhone = PhoneNumber.Create(contact.Phone);
        if (contactPhone.IsFailure)
        {
            return Result.Failure<StudentDetails>(ValidationErrors.ForField("emergencyContact.phone", contactPhone.Error));
        }

        var emergencyContact = EmergencyContact.Create(contact.Name, contactPhone.Value, contact.Relationship);
        if (emergencyContact.IsFailure)
        {
            return Result.Failure<StudentDetails>(ValidationErrors.ForField("emergencyContact", emergencyContact.Error));
        }

        return Result.Success(new StudentDetails(
            input.FirstName, input.LastName, input.BirthDate, input.Gender, phone.Value, email.Value, emergencyContact.Value));
    }

    /// <summary>Field-level domain failures become <c>Validation.Failed</c> on the field they concern; rule failures pass through.</summary>
    public static Error ToStudentFieldError(this Error error, IStudentFields input)
    {
        var field = error switch
        {
            _ when error == CommonErrors.FieldRequired || error == CommonErrors.FieldTooLong =>
                PersonNames.Validate(input.FirstName, "-").IsFailure ? "firstName" : "lastName",
            _ when error == CommonErrors.FieldInvalid => Enum.IsDefined(input.Gender) ? "birthDate" : "gender",
            _ when error == StudentErrors.EmergencyContactRequired => "emergencyContact",
            _ => null,
        };
        return field is null ? error : ValidationErrors.ForField(field, error);
    }

    public static Result<PhoneNumber?> OptionalPhone(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Success<PhoneNumber?>(null);
        }

        var phone = PhoneNumber.Create(value);
        return phone.IsSuccess ? Result.Success<PhoneNumber?>(phone.Value) : Result.Failure<PhoneNumber?>(ValidationErrors.ForField(field, phone.Error));
    }

    public static Result<Email?> OptionalEmail(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Success<Email?>(null);
        }

        var email = Email.Create(value);
        return email.IsSuccess ? Result.Success<Email?>(email.Value) : Result.Failure<Email?>(ValidationErrors.ForField(field, email.Error));
    }
}

/// <summary>Validated guardian data turned into value objects; <paramref name="Field"/> prefixes field errors.</summary>
internal sealed record GuardianValues(string FirstName, string LastName, PhoneNumber Phone, Email? Email, Address? Address)
{
    public static Result<GuardianValues> From(IGuardianFields input, string field)
    {
        var prefix = field.Length == 0 ? string.Empty : field + ".";
        var phone = PhoneNumber.Create(input.Phone);
        if (phone.IsFailure)
        {
            return Result.Failure<GuardianValues>(ValidationErrors.ForField(prefix + "phone", phone.Error));
        }

        var email = StudentInputMapping.OptionalEmail(input.Email, prefix + "email");
        if (email.IsFailure)
        {
            return Result.Failure<GuardianValues>(email.Error);
        }

        Address? address = null;
        if (input.Address is { } a)
        {
            var created = Address.Create(a.Line, a.District, a.City);
            if (created.IsFailure)
            {
                return Result.Failure<GuardianValues>(ValidationErrors.ForField(prefix + "address", created.Error));
            }

            address = created.Value;
        }

        return Result.Success(new GuardianValues(input.FirstName, input.LastName, phone.Value, email.Value, address));
    }

    public Result<Guardian> Create(SchoolId tenantId) => Guardian.Create(tenantId, FirstName, LastName, Phone, Email, Address);

    public Result UpdateOf(Guardian guardian) => guardian.Update(FirstName, LastName, Phone, Email, Address);
}

/// <summary>
/// Turns guardian inputs into links: an existing id must belong to the school (Guardian.NotFound otherwise);
/// a new guardian whose phone already exists in the school links that guardian instead of creating a
/// copy, so siblings share it (US-13).
/// </summary>
internal static class GuardianLinkResolver
{
    public static async Task<Result<IReadOnlyCollection<GuardianLink>>> ResolveAsync(
        IReadOnlyList<GuardianLinkInput> inputs,
        SchoolId tenantId,
        IGuardianRepository guardians,
        CancellationToken cancellationToken)
    {
        var links = new List<GuardianLink>(inputs.Count);
        var createdInThisRequest = new Dictionary<PhoneNumber, GuardianId>();

        // DEC-388: the guardians named by id are locked first and in id order, like unlinking locks them, so the two
        // never wait for each other in a cycle (taking FOR SHARE again below is a no-op).
        foreach (var id in inputs.Where(i => i.GuardianId is not null).Select(i => i.GuardianId!.Value).Distinct().Order())
        {
            await guardians.LockForLinkAsync(GuardianId.From(id), cancellationToken);
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input.GuardianId is { } rawId)
            {
                var id = GuardianId.From(rawId);
                // DEC-368: locked first, so a guardian being deleted as an orphan is either kept or not found.
                await guardians.LockForLinkAsync(id, cancellationToken);
                if (!await guardians.ExistsAsync(id, cancellationToken))
                {
                    return Result.Failure<IReadOnlyCollection<GuardianLink>>(GuardianErrors.NotFound);
                }

                links.Add(new GuardianLink(id, input.Relationship));
                continue;
            }

            var values = GuardianValues.From(input.NewGuardian!, $"guardians[{i}].newGuardian");
            if (values.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<GuardianLink>>(values.Error);
            }

            var phone = values.Value.Phone;
            GuardianId? existing = null;
            if (createdInThisRequest.TryGetValue(phone, out var pending))
            {
                existing = pending;
            }
            else if (await guardians.FindIdByPhoneAsync(phone, cancellationToken) is { } found)
            {
                // DEC-368: deleted meanwhile as an orphan? Then a new guardian is created below.
                await guardians.LockForLinkAsync(found, cancellationToken);
                existing = await guardians.ExistsAsync(found, cancellationToken) ? found : null;
            }

            if (existing is { } existingId)
            {
                links.Add(new GuardianLink(existingId, input.Relationship));
                continue;
            }

            var created = values.Value.Create(tenantId);
            if (created.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<GuardianLink>>(created.Error);
            }

            guardians.Add(created.Value);
            createdInThisRequest[phone] = created.Value.Id;
            links.Add(new GuardianLink(created.Value.Id, input.Relationship));
        }

        // E.g. an existing id plus a "new" guardian with the same phone: one guardian linked twice.
        return links.DistinctBy(l => l.GuardianId).Count() == links.Count
            ? Result.Success<IReadOnlyCollection<GuardianLink>>(links)
            : Result.Failure<IReadOnlyCollection<GuardianLink>>(ValidationErrors.ForField("guardians", CommonErrors.FieldInvalid));
    }

    /// <summary>A duplicate or too many links is a field error on <c>guardians</c>; rule failures pass through.</summary>
    public static Error ToGuardiansFieldError(this Error error) =>
        error == CommonErrors.FieldInvalid ? ValidationErrors.ForField("guardians", error) : error;
}

/// <summary>
/// DEC-157: two requests can both decide that a new guardian's phone is unknown and both insert it; the
/// unique index rejects the second with Guardian.PhoneTaken. Running the command once more, in a fresh
/// transaction, finds the committed guardian and links it (US-13) instead of failing.
/// </summary>
public static class GuardianLinkConflicts
{
    public static async Task<Result<T>> RetryOnceAsync<T>(Func<Task<Result<T>>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await operation();
        return result.IsFailure && result.Error == GuardianErrors.PhoneTaken ? await operation() : result;
    }
}
