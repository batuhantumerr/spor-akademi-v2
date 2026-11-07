using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using SporAkademi.Application.Identity;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (NFR-SEC-03, DEC-051); the user argument is unused by it.</summary>
internal sealed class AspNetPasswordHasher : IPasswordHasher
{
    private static readonly object NoUser = new();
    private readonly PasswordHasher<object> _hasher = new();
    private readonly Lazy<string> _dummyHash;

    public AspNetPasswordHasher() =>
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(NoUser, RandomNumberGenerator.GetHexString(32)));

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public PasswordVerification Verify(string passwordHash, string providedPassword) =>
        _hasher.VerifyHashedPassword(NoUser, passwordHash, providedPassword) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };

    public void SpendVerificationTime(string providedPassword) =>
        _hasher.VerifyHashedPassword(NoUser, _dummyHash.Value, providedPassword);
}

/// <summary>Opaque refresh tokens: 256 random bits, base64url; stored only as a lowercase hex SHA-256 (DEC-053).</summary>
internal sealed class RefreshTokenFactory : IRefreshTokenFactory
{
    private const int TokenBytes = 32;

    public NewRefreshToken Create()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return new NewRefreshToken(token, Hash(token));
    }

    public string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}

/// <summary>Set-password tokens (DEC-352): like refresh tokens, 256 random bits, base64url, stored as a lowercase hex SHA-256.</summary>
internal sealed class PasswordTokenFactory(IRefreshTokenFactory opaqueTokens) : IPasswordTokenFactory
{
    public NewPasswordToken Create()
    {
        var token = opaqueTokens.Create();
        return new NewPasswordToken(token.Token, token.Hash);
    }

    public string Hash(string token) => opaqueTokens.Hash(token);
}
