namespace SporAkademi.Domain.SharedKernel;

/// <summary>Postal address: street line, district (ilçe) and city (il).</summary>
public sealed record Address
{
    public const int LineMaxLength = 200;
    public const int PartMaxLength = 60;

    private Address(string line, string district, string city)
    {
        Line = line;
        District = district;
        City = city;
    }

    // For EF Core materialization only.
    private Address()
    {
        Line = null!;
        District = null!;
        City = null!;
    }

    public string Line { get; private init; }

    public string District { get; private init; }

    public string City { get; private init; }

    public static Result<Address> Create(string line, string district, string city)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(district);
        ArgumentNullException.ThrowIfNull(city);

        var l = TextNormalization.CollapseSpaces(line);
        var d = TextNormalization.CollapseSpaces(district);
        var c = TextNormalization.CollapseSpaces(city);
        if (l.Length == 0 || d.Length == 0 || c.Length == 0)
        {
            return Result.Failure<Address>(CommonErrors.FieldRequired);
        }

        if (l.Length > LineMaxLength || d.Length > PartMaxLength || c.Length > PartMaxLength)
        {
            return Result.Failure<Address>(CommonErrors.FieldTooLong);
        }

        return Result.Success(new Address(l, d, c));
    }
}
