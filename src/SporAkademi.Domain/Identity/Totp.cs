using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SporAkademi.Domain.Identity;

/// <summary>
/// Time-based one-time passwords, RFC 6238 with the parameters every authenticator app supports: HMAC-SHA-1, six
/// digits, 30 second steps (P5-01, DEC-600). Written here instead of taking a package: it is a few lines over
/// <see cref="HMACSHA1"/>, checked against the RFC's test vectors.
/// </summary>
public static class Totp
{
    public const int SecretBytes = 20; // 160 bits, RFC 4226 section 4
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>One step either side of the current one is accepted (clock drift between phone and server).</summary>
    public const int ToleranceSteps = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The number of 30 second steps since the Unix epoch at <paramref name="time"/>.</summary>
    public static long StepOf(DateTimeOffset time) => Math.DivRem(time.ToUnixTimeSeconds(), StepSeconds, out _);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 and every authenticator app use HMAC-SHA-1; as an HMAC over a 160-bit secret it is not weakened by SHA-1 collisions (DEC-600).")]
    public static string Compute(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, mac);

        // RFC 4226 dynamic truncation.
        var offset = mac[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(mac[offset..]) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Finds the step (within <see cref="ToleranceSteps"/> of <paramref name="now"/>) whose code is
    /// <paramref name="code"/>. All three candidates are always computed and compared in constant time. The caller
    /// remembers the step it accepted and refuses it, and every earlier one, afterwards (replay).
    /// </summary>
    public static bool TryMatch(ReadOnlySpan<byte> secret, string code, DateTimeOffset now, out long step)
    {
        step = 0;
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var given = Encoding.ASCII.GetBytes(code);
        var current = StepOf(now);
        var matched = false;
        for (var candidate = current - ToleranceSteps; candidate <= current + ToleranceSteps; candidate++)
        {
            var expected = Encoding.ASCII.GetBytes(Compute(secret, candidate));
            if (CryptographicOperations.FixedTimeEquals(expected, given) && (!matched || candidate > step))
            {
                (matched, step) = (true, candidate);
            }
        }

        return matched;
    }

    /// <summary>The <c>otpauth://</c> URI an authenticator app reads from the QR code (Google Authenticator key URI format).</summary>
    public static string ProvisioningUri(string issuer, string account, ReadOnlySpan<byte> secret)
    {
        var escapedIssuer = Uri.EscapeDataString(issuer);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{escapedIssuer}:{Uri.EscapeDataString(account)}?secret={Base32.Encode(secret)}&issuer={escapedIssuer}&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
    }
}

/// <summary>RFC 4648 Base32 (upper case, no padding on output), the alphabet authenticator apps use for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    /// <summary>Decodes text typed by a person: case and spaces, dashes and padding are ignored. Invalid characters throw <see cref="FormatException"/>.</summary>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-' or '=')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException("Not a Base32 character.");
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}
