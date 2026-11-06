using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>
/// An expected failure. <see cref="Code"/> is a stable API contract (appendix E); the client builds
/// the Turkish message from it. <see cref="Description"/> is for developers and logs only.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "DEC-096: 'Error' is the ubiquitous name for this type; the only exception under src/.")]
public sealed record Error
{
    private Error(string code, string description, ErrorType type, ImmutableDictionary<string, object?> arguments)
    {
        Code = code;
        Description = description;
        Type = type;
        Arguments = arguments;
    }

    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Validation, ImmutableDictionary<string, object?>.Empty);

    public string Code { get; }

    public string Description { get; }

    public ErrorType Type { get; }

    /// <summary>Values for placeholders in the client message, e.g. <c>{groupName}</c>.</summary>
    public ImmutableDictionary<string, object?> Arguments { get; }

    public static Error Validation(string code, string description) => Create(code, description, ErrorType.Validation);

    public static Error Unauthorized(string code, string description) => Create(code, description, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string description) => Create(code, description, ErrorType.Forbidden);

    public static Error NotFound(string code, string description) => Create(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => Create(code, description, ErrorType.Conflict);

    public static Error BusinessRule(string code, string description) => Create(code, description, ErrorType.BusinessRule);

    public Error WithArgument(string name, object? value) =>
        new(Code, Description, Type, Arguments.SetItem(name, value));

    public bool Equals(Error? other) =>
        other is not null && Code == other.Code && Type == other.Type;

    public override int GetHashCode() => HashCode.Combine(Code, Type);

    private static Error Create(string code, string description, ErrorType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(description);
        return new Error(code, description, type, ImmutableDictionary<string, object?>.Empty);
    }
}
