using System.Globalization;
using System.Text;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>Turkish-aware text keys for uniqueness and search (DEC-013).</summary>
public static class TextNormalization
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Case-insensitive key with Turkish casing rules: "İzmir" and "izmir" match.</summary>
    public static string CaseKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CollapseSpaces(value).ToLower(Turkish);
    }

    /// <summary>Search key that also ignores Turkish letters: "Şükrü" and "sukru" match (US-16).</summary>
    public static string SearchKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var lower = CaseKey(value);
        var builder = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            builder.Append(c switch
            {
                'ç' => 'c',
                'ğ' => 'g',
                'ı' => 'i',
                'ö' => 'o',
                'ş' => 's',
                'ü' => 'u',
                'â' => 'a',
                'î' => 'i',
                'û' => 'u',
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>Trims and collapses runs of whitespace into single spaces.</summary>
    public static string CollapseSpaces(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
