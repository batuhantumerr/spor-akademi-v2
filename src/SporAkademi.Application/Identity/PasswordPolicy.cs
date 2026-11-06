using System.Collections.Frozen;
using System.Security.Cryptography;

namespace SporAkademi.Application.Identity;

/// <summary>NFR-SEC-04: at least 10 characters, no character-class rules, not a common password.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    /// <summary>Upper bound so hashing cost stays bounded.</summary>
    public const int MaxLength = 128;

    private static readonly Lazy<FrozenSet<string>> CommonPasswords = new(LoadCommonPasswords);

    public static bool IsStrongEnough(string? password) =>
        password is { Length: >= MinLength and <= MaxLength }
        && !CommonPasswords.Value.Contains(password.Trim());

    private static FrozenSet<string> LoadCommonPasswords()
    {
        using var stream = typeof(PasswordPolicy).Assembly.GetManifestResourceStream("SporAkademi.Application.Identity.common-passwords.txt")
            ?? throw new InvalidOperationException("The common password list is not embedded.");
        using var reader = new StreamReader(stream);
        var entries = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var entry = line.Trim();
            if (entry.Length > 0 && !entry.StartsWith('#'))
            {
                entries.Add(entry);
            }
        }

        return entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Temporary passwords an admin hands over once (US-05); they satisfy the policy.</summary>
public static class TemporaryPassword
{
    // No look-alike characters (0/O, 1/l/I), so it can be read out or typed from a note.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    public const int Length = 16;

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}
