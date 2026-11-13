namespace SporAkademi.Application.Abstractions;

/// <summary>
/// Strict parsing of enum names from client input: exactly one declared name, ordinal. <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>
/// also accepts numbers, surrounding spaces and comma-separated combinations ("A,B"), which must never reach storage as a
/// "code" (P4-05, DEC-533).
/// </summary>
public static class EnumNames
{
    public static bool TryParseExact<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        if (value is null || !Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal))
        {
            return false;
        }

        parsed = Enum.Parse<TEnum>(value);
        return true;
    }

    public static bool IsExact<TEnum>(string? value)
        where TEnum : struct, Enum => TryParseExact<TEnum>(value, out _);
}
