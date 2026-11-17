using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    public bool RequirePlatformAdmin { get; set; } = true;

    /// <summary>Optional: without it the key is derived from <c>Jwt:SigningKey</c> (DEC-602).</summary>
    public string? EncryptionKey { get; set; }

    public static bool IsValidKey(string? key) =>
        key is { Length: 64 } && key.All(char.IsAsciiHexDigit);

    /// <summary>
    /// The mandatory MFA of the Platform Admin may be switched off only where nothing real is protected: a production
    /// host refuses to start with it off.
    /// </summary>
    public static bool MayBeSwitchedOff(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}

internal sealed class MfaPolicy(IOptions<MfaOptions> options) : IMfaPolicy
{
    public bool IsRequired(UserRole role) => role == UserRole.PlatformAdmin && options.Value.RequirePlatformAdmin;
}

/// <summary>
/// AES-256-GCM over the TOTP secret (P5-01, DEC-602). The key is <c>Mfa:EncryptionKey</c>, or, when that is not set,
/// derived from <c>Jwt:SigningKey</c> with HKDF-SHA-256 under a fixed label, so it differs from the signing key itself. The
/// text is <c>v1.</c> and base64url of nonce, tag and ciphertext; the user's id is authenticated with it, so a ciphertext
/// moved to another user's row does not decrypt.
/// </summary>
internal sealed partial class MfaSecretProtector : IMfaSecretProtector
{
    private const string Version = "v1.";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string DerivationLabel = "spor-akademi/mfa-secret-key/v1";

    private readonly byte[] _key;

    public MfaSecretProtector(IOptions<MfaOptions> options, IConfiguration configuration, IHostEnvironment environment, ILogger<MfaSecretProtector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        var configured = options.Value.EncryptionKey;
        if (MfaOptions.IsValidKey(configured))
        {
            _key = Convert.FromHexString(configured!);
            return;
        }

        var signingKey = configuration["Jwt:SigningKey"];
        if (string.IsNullOrEmpty(signingKey) || signingKey.Length % 2 != 0 || !signingKey.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("Mfa:EncryptionKey is not set and Jwt:SigningKey cannot stand in for it.");
        }

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(signingKey), 32, salt: [], info: Encoding.ASCII.GetBytes(DerivationLabel));
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            LogDerivedKey(logger, MfaOptions.Section + ":EncryptionKey");
        }
    }

    public string Protect(Guid userId, ReadOnlySpan<byte> secret)
    {
        var blob = new byte[NonceBytes + TagBytes + secret.Length];
        var nonce = blob.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, secret, blob.AsSpan(NonceBytes + TagBytes), blob.AsSpan(NonceBytes, TagBytes), AssociatedData(userId));
        return Version + Base64Url.EncodeToString(blob);
    }

    public byte[] Unprotect(Guid userId, string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!ciphertext.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown secret format.");
        }

        byte[] blob;
        try
        {
            blob = Base64Url.DecodeFromChars(ciphertext.AsSpan(Version.Length));
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Malformed secret.", exception);
        }

        if (blob.Length <= NonceBytes + TagBytes)
        {
            throw new CryptographicException("Malformed secret.");
        }

        var plain = new byte[blob.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(blob.AsSpan(0, NonceBytes), blob.AsSpan(NonceBytes + TagBytes), blob.AsSpan(NonceBytes, TagBytes), plain, AssociatedData(userId));
        return plain;
    }

    private static byte[] AssociatedData(Guid userId) =>
        Encoding.ASCII.GetBytes($"mfa-secret/v1:{userId:N}");

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Setting} is not set: the MFA secrets are encrypted with a key derived from the JWT signing key, so rotating that key would lock out every enrolled user")]
    private static partial void LogDerivedKey(ILogger logger, string setting);
}

internal static class MfaRegistration
{
    public static IServiceCollection AddMfa(this IServiceCollection services)
    {
        services.AddOptions<MfaOptions>()
            .BindConfiguration(MfaOptions.Section)
            .Validate(o => string.IsNullOrEmpty(o.EncryptionKey) || MfaOptions.IsValidKey(o.EncryptionKey), "Mfa:EncryptionKey must be 32 bytes of hex (64 characters).")
            .Validate<IHostEnvironment>(
                (o, environment) => o.RequirePlatformAdmin || MfaOptions.MayBeSwitchedOff(environment),
                "Mfa:RequirePlatformAdmin can be switched off in Development only; the Platform Admin must have MFA elsewhere.")
            .ValidateOnStart();
        services.AddSingleton<IMfaPolicy, MfaPolicy>();
        services.AddSingleton<IMfaSecretProtector, MfaSecretProtector>();
        return services;
    }
}
