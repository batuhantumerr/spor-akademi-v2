using System.Security.Cryptography;
using System.Text;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Mfa;

/// <summary>
/// Encrypts the TOTP secret at rest (P5-01, DEC-602). The user's id is bound to the ciphertext, so a secret copied
/// from one row to another does not decrypt.
/// </summary>
public interface IMfaSecretProtector
{
    string Protect(Guid userId, ReadOnlySpan<byte> secret);

    /// <summary>Throws <see cref="CryptographicException"/> when the ciphertext is not ours, was altered or belongs to another user.</summary>
    byte[] Unprotect(Guid userId, string ciphertext);
}

/// <summary>Which roles must have a second factor (P5-01, DEC-604, DEC-606).</summary>
public interface IMfaPolicy
{
    bool IsRequired(UserRole role);
}

/// <summary>What the browser needs to set up an authenticator: the <c>otpauth://</c> URI (for the QR code) and the secret as text.</summary>
public sealed record MfaEnrollmentStarted(string OtpAuthUri, string Secret);

/// <summary>The recovery codes, shown once; only their hashes are stored (DEC-603).</summary>
public sealed record MfaRecoveryCodes(IReadOnlyList<string> Codes);

/// <summary>A login stopped after the password: the second step is still due.</summary>
/// <param name="Purpose"><see cref="MfaChallengePurpose.Verify"/> asks for a code; <see cref="MfaChallengePurpose.Enroll"/> for a first enrolment.</param>
public sealed record MfaChallengeIssued(string Token, DateTimeOffset ExpiresAt, MfaChallengePurpose Purpose);

/// <summary>The result of a login: a session, or a challenge for the second step.</summary>
public sealed record LoginOutcome(AuthSession? Session, MfaChallengeIssued? Challenge)
{
    public static LoginOutcome Authenticated(AuthSession session) => new(session, null);

    public static LoginOutcome SecondStep(MfaChallengeIssued challenge) => new(null, challenge);
}

/// <summary>The caller's own MFA state (<c>GET /me/mfa</c>).</summary>
/// <param name="Available">Whether the role may use MFA at all (School Admin and Platform Admin).</param>
/// <param name="Required">Whether the role must have it: it then cannot be turned off.</param>
public sealed record MfaStatus(bool Available, bool Enabled, bool Required, int RecoveryCodesRemaining);

/// <summary>Recovery codes and code parsing (P5-01, DEC-603).</summary>
internal static class MfaCodes
{
    /// <summary>10 random bytes: 80 bits, 16 Base32 characters shown as four groups.</summary>
    private const int RecoveryCodeBytes = 10;

    public static string NewRecoveryCode()
    {
        var text = Base32.Encode(RandomNumberGenerator.GetBytes(RecoveryCodeBytes));
        return string.Join('-', Enumerable.Range(0, 4).Select(i => text.Substring(i * 4, 4)));
    }

    /// <summary>What a person types is forgiving: case, spaces and dashes do not matter.</summary>
    public static string Normalize(string? code) =>
        string.Concat((code ?? string.Empty).Where(c => c is not (' ' or '-' or '\t'))).ToUpperInvariant();

    public static bool LooksLikeTotp(string normalized) =>
        normalized.Length == Totp.Digits && normalized.All(char.IsAsciiDigit);

    /// <summary>
    /// A plain SHA-256 of the user's id and the code: the code has 80 random bits, so a slow hash adds nothing, and the id
    /// keeps two users' equal hashes apart.
    /// </summary>
    public static string HashRecoveryCode(UserId userId, string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId.Value:N}:{normalized}")));
}

internal static class MfaSecrets
{
    /// <summary>The secret in clear, or <c>null</c> when it cannot be decrypted (a changed key): reported as a stable error, never a 500.</summary>
    public static byte[]? TryUnprotect(IMfaSecretProtector protector, UserId userId, string ciphertext)
    {
        try
        {
            return protector.Unprotect(userId.Value, ciphertext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Starting and confirming an enrolment; shared by the signed-in and the login-challenge routes.</summary>
internal sealed class MfaEnrollment(IMfaSecretProtector protector)
{
    public const string Issuer = "Spor Akademi";

    public Result<MfaEnrollmentStarted> Start(User user, DateTimeOffset now)
    {
        var secret = Totp.NewSecret();
        var begun = user.BeginMfaEnrollment(protector.Protect(user.Id.Value, secret), now);
        return begun.IsSuccess
            ? Result.Success(new MfaEnrollmentStarted(Totp.ProvisioningUri(Issuer, user.Email.Value, secret), Base32.Encode(secret)))
            : Result.Failure<MfaEnrollmentStarted>(begun.Error);
    }

    /// <summary>
    /// Checks the code against the pending secret and, if right, turns MFA on. <see cref="MfaErrors.CodeInvalid"/> for a
    /// wrong code: the caller counts it.
    /// </summary>
    public Result<MfaRecoveryCodes> Confirm(User user, string code, DateTimeOffset now)
    {
        if (user.MfaEnabled)
        {
            return Result.Failure<MfaRecoveryCodes>(MfaErrors.AlreadyEnabled);
        }

        if (user.MfaCredential is not { } credential)
        {
            return Result.Failure<MfaRecoveryCodes>(MfaErrors.EnrollmentNotStarted);
        }

        var normalized = MfaCodes.Normalize(code);
        if (!MfaCodes.LooksLikeTotp(normalized))
        {
            return Result.Failure<MfaRecoveryCodes>(MfaErrors.CodeInvalid);
        }

        var secret = MfaSecrets.TryUnprotect(protector, user.Id, credential.SecretCiphertext);
        if (secret is null)
        {
            return Result.Failure<MfaRecoveryCodes>(MfaErrors.SecretUnreadable);
        }

        if (!Totp.TryMatch(secret, normalized, now, out var step))
        {
            return Result.Failure<MfaRecoveryCodes>(MfaErrors.CodeInvalid);
        }

        var codes = Enumerable.Range(0, User.RecoveryCodeCount).Select(_ => MfaCodes.NewRecoveryCode()).ToList();
        var confirmed = user.ConfirmMfaEnrollment(step, [.. codes.Select(c => MfaCodes.HashRecoveryCode(user.Id, MfaCodes.Normalize(c)))], now);
        return confirmed.IsSuccess
            ? Result.Success(new MfaRecoveryCodes(codes))
            : Result.Failure<MfaRecoveryCodes>(confirmed.Error);
    }
}

/// <summary>Checks a code of a user who has MFA: a TOTP code (once only) or an unused recovery code.</summary>
internal sealed class MfaCodeVerifier(IMfaSecretProtector protector)
{
    /// <summary>Success uses the code up. <see cref="MfaErrors.CodeInvalid"/> otherwise; the caller counts the failure.</summary>
    public Result Verify(User user, string code, DateTimeOffset now)
    {
        if (user.MfaCredential is not { IsConfirmed: true } credential)
        {
            return Result.Failure(MfaErrors.CodeInvalid);
        }

        var normalized = MfaCodes.Normalize(code);
        if (!MfaCodes.LooksLikeTotp(normalized))
        {
            return user.UseRecoveryCode(MfaCodes.HashRecoveryCode(user.Id, normalized), now);
        }

        var secret = MfaSecrets.TryUnprotect(protector, user.Id, credential.SecretCiphertext);
        if (secret is null)
        {
            return Result.Failure(MfaErrors.SecretUnreadable);
        }

        return Totp.TryMatch(secret, normalized, now, out var step)
            ? user.AcceptMfaStep(step)
            : Result.Failure(MfaErrors.CodeInvalid);
    }
}
