namespace SporAkademi.Domain.SharedKernel;

public sealed record Email
{
    public const int MaxLength = 254;
    private const int MaxLocalPartLength = 64;

    public static readonly Error Invalid = Error.Validation("Email.Invalid", "The e-mail address is not valid.");

    private Email(string value) => Value = value;

    public string Value { get; }

    public static Result<Email> Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return Result.Failure<Email>(CommonErrors.FieldRequired);
        }

        return IsValid(normalized) ? Result.Success(new Email(normalized)) : Result.Failure<Email>(Invalid);
    }

    public override string ToString() => Value;

    private static bool IsValid(string email)
    {
        if (email.Length > MaxLength || email.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != email.LastIndexOf('@') || at > MaxLocalPartLength)
        {
            return false;
        }

        var domain = email[(at + 1)..];
        return domain.Contains('.', StringComparison.Ordinal)
            && !domain.StartsWith('.')
            && !domain.EndsWith('.')
            && !domain.Contains("..", StringComparison.Ordinal);
    }
}
