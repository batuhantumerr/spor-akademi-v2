using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Identity.Mfa;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>
/// Settings of the second factor (P5-01). <see cref="RequirePlatformAdmin"/> is on unless it is switched off in
/// Development (DEC-608); <see cref="EncryptionKey"/> is 32 bytes of hex for the TOTP secrets (DEC-602).
/// </summary>
internal sealed class MfaOptions
{
    public const string Section = "Mfa";

    /// <summary>The id a deployment that never rotated has: its <c>v1.</c> secrets are read with the one key and rewritten under this id.</summary>
    public const string DefaultKeyId = "k1";

    public bool RequirePlatformAdmin { get; set; } = true;

    /// <summary>Required outside Development and Testing, where it is otherwise derived from <c>Jwt:SigningKey</c> (DEC-602).</summary>
    public string? EncryptionKey { get; set; }

    /// <summary>Names the current key in the ciphertext (V1-03, DEC-685); change it together with <see cref="EncryptionKey"/>.</summary>
    public string EncryptionKeyId { get; set; } = DefaultKeyId;

    /// <summary>
    /// Keys that were current once (id => 32 bytes of hex): they only decrypt, until nothing is stored under them any more
    /// (V1-03, DEC-685). Bound from <c>Mfa:PreviousEncryptionKeys:&lt;id&gt;</c>.
    /// </summary>
    public Dictionary<string, string> PreviousEncryptionKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidKey(string? key) =>
        key is { Length: 64 } && key.All(char.IsAsciiHexDigit);

    /// <summary>1 to 16 letters or digits: it sits between dots in the ciphertext and in an environment variable name.</summary>
    public static bool IsValidKeyId(string? id) =>
        id is { Length: >= 1 and <= 16 } && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>What is wrong with the key settings, or <c>null</c> (V1-03, DEC-686): bad ids, bad hex, an id used twice.</summary>
    public string? KeySettingsProblem()
    {
        if (!string.IsNullOrEmpty(EncryptionKey) && !IsValidKey(EncryptionKey))
        {
            return "Mfa:EncryptionKey must be 32 bytes of hex (64 characters).";
        }

        if (!IsValidKeyId(EncryptionKeyId))
        {
            return "Mfa:EncryptionKeyId must be 1 to 16 letters or digits.";
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EncryptionKeyId };
        foreach (var (id, key) in PreviousEncryptionKeys)
        {
            if (!IsValidKeyId(id))
            {
                return "Mfa:PreviousEncryptionKeys ids must be 1 to 16 letters or digits.";
            }

            if (!IsValidKey(key))
            {
                return "Every Mfa:PreviousEncryptionKeys value must be 32 bytes of hex (64 characters).";
            }

            if (!ids.Add(id))
            {
                return "Mfa key ids must be unique: a previous key uses the id of the current one.";
            }
        }

        return null;
    }

    /// <summary>
    /// The mandatory MFA of the Platform Admin may be switched off only where nothing real is protected: a production
    /// host refuses to start with it off.
    /// </summary>
    public static bool MayBeSwitchedOff(IHostEnvironment environment) => environment.IsDevelopment();

    /// <summary>
    /// Only a developer's machine and the test host may stand in for the encryption key with one derived from the JWT key;
    /// everywhere else <c>Mfa:EncryptionKey</c> is required (DEC-602).
    /// </summary>
    public static bool MayDeriveKey(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}

internal sealed class MfaPolicy(IOptions<MfaOptions> options) : IMfaPolicy
{
    public bool IsRequired(UserRole role) => role == UserRole.PlatformAdmin && options.Value.RequirePlatformAdmin;
}

/// <summary>
/// AES-256-GCM over the TOTP secret (P5-01, DEC-602; rotation V1-03, DEC-685). The current key is <c>Mfa:EncryptionKey</c>
/// (named by <c>Mfa:EncryptionKeyId</c>), or, when that is not set, derived from <c>Jwt:SigningKey</c> with HKDF-SHA-256
/// under a fixed label, so it differs from the signing key itself. <c>Mfa:PreviousEncryptionKeys</c> only decrypt. The text
/// is <c>v2.&lt;key id&gt;.</c> and base64url of nonce, tag and ciphertext; the user's id and the key id are authenticated
/// with it, so a ciphertext moved to another user's row does not decrypt. The first format, <c>v1.</c> without a key id,
/// is still read (with each key in turn) and is always due for re-encryption.
/// </summary>
internal sealed class MfaSecretProtector : IMfaSecretProtector
{
    private const string LegacyVersion = "v1.";
    private const string Version = "v2.";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string DerivationLabel = "spor-akademi/mfa-secret-key/v1";

    private readonly byte[] _currentKey;
    private readonly string _currentId;
    private readonly Dictionary<string, byte[]> _previousKeys = new(StringComparer.OrdinalIgnoreCase);

    public MfaSecretProtector(IOptions<MfaOptions> options, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var settings = options.Value;
        if (settings.KeySettingsProblem() is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        _currentId = settings.EncryptionKeyId.ToLowerInvariant();
        foreach (var (id, key) in settings.PreviousEncryptionKeys)
        {
            _previousKeys[id.ToLowerInvariant()] = Convert.FromHexString(key);
        }

        if (MfaOptions.IsValidKey(settings.EncryptionKey))
        {
            _currentKey = Convert.FromHexString(settings.EncryptionKey!);
            return;
        }

        if (!MfaOptions.MayDeriveKey(environment))
        {
            throw new InvalidOperationException("Mfa:EncryptionKey must be set (32 bytes of hex) outside Development and Testing.");
        }

        var signingKey = configuration["Jwt:SigningKey"];
        if (string.IsNullOrEmpty(signingKey) || signingKey.Length % 2 != 0 || !signingKey.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("Mfa:EncryptionKey is not set and Jwt:SigningKey cannot stand in for it.");
        }

        _currentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(signingKey), 32, salt: [], info: Encoding.ASCII.GetBytes(DerivationLabel));
    }

    public string Protect(Guid userId, ReadOnlySpan<byte> secret)
    {
        var blob = new byte[NonceBytes + TagBytes + secret.Length];
        var nonce = blob.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_currentKey, TagBytes);
        aes.Encrypt(nonce, secret, blob.AsSpan(NonceBytes + TagBytes), blob.AsSpan(NonceBytes, TagBytes), AssociatedData(userId, _currentId));
        return $"{Version}{_currentId}.{Base64Url.EncodeToString(blob)}";
    }

    public byte[] Unprotect(Guid userId, string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.StartsWith(Version, StringComparison.Ordinal))
        {
            var rest = ciphertext.AsSpan(Version.Length);
            var dot = rest.IndexOf('.');
            if (dot <= 0)
            {
                throw new CryptographicException("Malformed secret.");
            }

            var id = rest[..dot].ToString().ToLowerInvariant();
            var key = id == _currentId ? _currentKey : _previousKeys.GetValueOrDefault(id);
            return key is null
                ? throw new CryptographicException("The secret was encrypted with a key that is not configured.")
                : Decrypt(key, AssociatedData(userId, id), Decode(rest[(dot + 1)..]));
        }

        if (ciphertext.StartsWith(LegacyVersion, StringComparison.Ordinal))
        {
            // No key id: any configured key may have written it, and the tag tells which (V1-03, DEC-685).
            var blob = Decode(ciphertext.AsSpan(LegacyVersion.Length));
            var associated = LegacyAssociatedData(userId);
            foreach (var key in new[] { _currentKey }.Concat(_previousKeys.Values))
            {
                try
                {
                    return Decrypt(key, associated, blob);
                }
                catch (CryptographicException)
                {
                    // The next key.
                }
            }

            throw new CryptographicException("The secret cannot be decrypted with any configured key.");
        }

        throw new CryptographicException("Unknown secret format.");
    }

    public bool IsCurrent(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ciphertext.StartsWith($"{Version}{_currentId}.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The prefix every secret under the current key starts with: what a re-encryption run looks for the opposite of.</summary>
    public string CurrentPrefix => $"{Version}{_currentId}.";

    private static byte[] Decode(ReadOnlySpan<char> text)
    {
        try
        {
            return Base64Url.DecodeFromChars(text);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Malformed secret.", exception);
        }
    }

    private static byte[] Decrypt(byte[] key, byte[] associated, byte[] blob)
    {
        if (blob.Length <= NonceBytes + TagBytes)
        {
            throw new CryptographicException("Malformed secret.");
        }

        var plain = new byte[blob.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(blob.AsSpan(0, NonceBytes), blob.AsSpan(NonceBytes + TagBytes), blob.AsSpan(NonceBytes, TagBytes), plain, associated);
        return plain;
    }

    private static byte[] AssociatedData(Guid userId, string keyId) =>
        Encoding.ASCII.GetBytes($"mfa-secret/v2:{keyId}:{userId:N}");

    private static byte[] LegacyAssociatedData(Guid userId) =>
        Encoding.ASCII.GetBytes($"mfa-secret/v1:{userId:N}");
}

internal static class MfaRegistration
{
    public static IServiceCollection AddMfa(this IServiceCollection services)
    {
        services.AddOptions<MfaOptions>()
            .BindConfiguration(MfaOptions.Section)
            .Validate(o => o.KeySettingsProblem() is null, "The Mfa key settings are invalid: keys are 32 bytes of hex (64 characters), key ids 1 to 16 letters or digits and unique (Mfa:EncryptionKey, Mfa:EncryptionKeyId, Mfa:PreviousEncryptionKeys).")
            .Validate<IHostEnvironment>(
                (o, environment) => MfaOptions.IsValidKey(o.EncryptionKey) || MfaOptions.MayDeriveKey(environment),
                "Mfa:EncryptionKey is required outside Development and Testing (32 bytes of hex).")
            .Validate<IHostEnvironment>(
                (o, environment) => o.RequirePlatformAdmin || MfaOptions.MayBeSwitchedOff(environment),
                "Mfa:RequirePlatformAdmin can be switched off in Development only; the Platform Admin must have MFA elsewhere.")
            .ValidateOnStart();
        services.AddSingleton<IMfaPolicy, MfaPolicy>();
        services.AddSingleton<MfaSecretProtector>();
        services.AddSingleton<IMfaSecretProtector>(provider => provider.GetRequiredService<MfaSecretProtector>());
        // Built at start, so a key that cannot be used stops the host here and not at the first administrator's login.
        services.AddHostedService<MfaKeyCheck>();
        services.AddScoped<MfaSecretRotationJob>();
        return services;
    }
}

/// <summary>
/// For a host that issues no sessions (P5-04): the MFA services exist so the container validates, but nothing can use them.
/// No key is read, validated or derived, so the worker needs neither <c>Mfa:EncryptionKey</c> nor <c>Jwt:SigningKey</c>.
/// </summary>
internal sealed class UnavailableMfa : IMfaPolicy, IMfaSecretProtector
{
    public bool IsRequired(UserRole role) => false;

    public string Protect(Guid userId, ReadOnlySpan<byte> secret) => throw Unsupported();

    public byte[] Unprotect(Guid userId, string ciphertext) => throw Unsupported();

    public bool IsCurrent(string ciphertext) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("This host does not handle MFA secrets.");
}

internal static class MfaUnavailableRegistration
{
    public static IServiceCollection AddMfaUnavailable(this IServiceCollection services)
    {
        services.AddSingleton<UnavailableMfa>();
        services.AddSingleton<IMfaPolicy>(provider => provider.GetRequiredService<UnavailableMfa>());
        services.AddSingleton<IMfaSecretProtector>(provider => provider.GetRequiredService<UnavailableMfa>());
        return services;
    }
}

/// <summary>Resolves the secret protector while the host starts (DEC-602).</summary>
internal sealed class MfaKeyCheck(IMfaSecretProtector protector) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = protector;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
