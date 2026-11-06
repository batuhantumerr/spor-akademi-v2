using System.Text.RegularExpressions;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>URL-safe school identifier: lowercase ASCII words joined by single hyphens, 3–50 chars.</summary>
public sealed partial record Slug
{
    public const int MinLength = 3;
    public const int MaxLength = 50;

    public static readonly Error Invalid = Error.Validation("Slug.Invalid", "The slug is not valid.");

    private Slug(string value) => Value = value;

    public string Value { get; }

    public static Result<Slug> Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<Slug>(CommonErrors.FieldRequired);
        }

        // \z, not $: $ also matches before a trailing newline.
        return value.Length is >= MinLength and <= MaxLength && Pattern().IsMatch(value)
            ? Result.Success(new Slug(value))
            : Result.Failure<Slug>(Invalid);
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
