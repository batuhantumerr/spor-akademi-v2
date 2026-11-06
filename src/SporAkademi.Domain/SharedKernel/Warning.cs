using System.Collections.Immutable;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>
/// Something the user should know about an operation that still succeeded (appendix D §1.8), e.g. two
/// groups sharing a facility (DEC-012). Like <see cref="Error"/>, <see cref="Code"/> is a stable contract
/// and the client builds the message from it and <see cref="Arguments"/>.
/// </summary>
public sealed record Warning
{
    private Warning(string code, Guid? relatedId, ImmutableDictionary<string, object?> arguments)
    {
        Code = code;
        RelatedId = relatedId;
        Arguments = arguments;
    }

    public string Code { get; }

    /// <summary>The other record involved, e.g. the group that uses the same facility.</summary>
    public Guid? RelatedId { get; }

    public ImmutableDictionary<string, object?> Arguments { get; }

    public static Warning Create(string code, Guid? relatedId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new Warning(code, relatedId, ImmutableDictionary<string, object?>.Empty);
    }

    public Warning WithArgument(string name, object? value) => new(Code, RelatedId, Arguments.SetItem(name, value));

    public bool Equals(Warning? other) => other is not null && Code == other.Code && RelatedId == other.RelatedId;

    public override int GetHashCode() => HashCode.Combine(Code, RelatedId);
}
