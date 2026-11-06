using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Students;

/// <summary>First and last names of students and guardians: trimmed, single-spaced, required, bounded.</summary>
public static class PersonNames
{
    public const int MaxLength = 100;

    public static Result<(string FirstName, string LastName)> Validate(string firstName, string lastName)
    {
        ArgumentNullException.ThrowIfNull(firstName);
        ArgumentNullException.ThrowIfNull(lastName);

        var first = TextNormalization.CollapseSpaces(firstName);
        var last = TextNormalization.CollapseSpaces(lastName);
        if (first.Length == 0 || last.Length == 0)
        {
            return Result.Failure<(string, string)>(CommonErrors.FieldRequired);
        }

        return first.Length > MaxLength || last.Length > MaxLength
            ? Result.Failure<(string, string)>(CommonErrors.FieldTooLong)
            : Result.Success((first, last));
    }
}
