namespace SporAkademi.Domain.SharedKernel;

/// <summary>A Turkish phone number normalized to E.164 (<c>+90</c> + 10 digits, first digit 2–5).</summary>
public sealed record PhoneNumber
{
    public const int MaxLength = 13;

    public static readonly Error Invalid = Error.Validation("PhoneNumber.Invalid", "The phone number is not valid.");

    private PhoneNumber(string value) => Value = value;

    public string Value { get; }

    public static Result<PhoneNumber> Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return Result.Failure<PhoneNumber>(CommonErrors.FieldRequired);
        }

        var hasPlus = trimmed.StartsWith('+');
        if (trimmed.LastIndexOf('+') > 0)
        {
            return Result.Failure<PhoneNumber>(Invalid);
        }

        var digits = new string([.. trimmed.Where(c => c is not (' ' or '-' or '(' or ')' or '.' or '+'))]);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return Result.Failure<PhoneNumber>(Invalid);
        }

        var national = digits switch
        {
            { Length: 12 } when digits.StartsWith("90", StringComparison.Ordinal) => digits[2..],
            { Length: 11 } when !hasPlus && digits[0] == '0' => digits[1..],
            { Length: 10 } when !hasPlus => digits,
            _ => null,
        };

        if (national is null || national[0] is < '2' or > '5')
        {
            return Result.Failure<PhoneNumber>(Invalid);
        }

        return Result.Success(new PhoneNumber("+90" + national));
    }

    public override string ToString() => Value;
}
