using System.Security.Cryptography;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>What one run of <see cref="MfaSecretRotationJob"/> found and did.</summary>
/// <param name="Reencrypted">Secrets moved to the current key by this run.</param>
/// <param name="Unreadable">Secrets no configured key opens (a retired key that was still in use, or damage): their owners need a reset.</param>
/// <param name="Remaining">Secrets still not under the current key after the run (including the unreadable ones): zero means the old keys can be retired.</param>
internal sealed record MfaRotationReport(int Reencrypted, int Unreadable, int Remaining);

/// <summary>
/// V1-03 (DEC-685): moves every TOTP secret that is not under the current key (an earlier key, or the first <c>v1.</c>
/// format) to it, so that an old key can be retired without waiting for every administrator to sign in. Idempotent: a
/// secret already under the current key is not touched, and each rewrite only applies if the row still holds the
/// ciphertext that was read (a user restarting an enrolment in the meantime wins). The run reports how many secrets remain
/// elsewhere in the log; the old key is retired when that is zero (docs/bulut-altyapisi.md, rotation runbook).
/// </summary>
internal sealed partial class MfaSecretRotationJob(TenantJobRunner runner, MfaSecretProtector protector, ILogger<MfaSecretRotationJob> logger)
{
    public const string JobId = "identity-mfa-secret-rotation";

    internal const int BatchSize = 200;

    /// <returns>The number of secrets moved to the current key.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Identity: re-encrypt MFA secrets under the current key")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var report = await ReencryptAsync(cancellationToken);
        if (report.Unreadable > 0)
        {
            LogUnreadable(logger, report.Unreadable);
        }

        if (report.Reencrypted > 0 || report.Remaining > 0)
        {
            LogReport(logger, report.Reencrypted, report.Remaining);
        }

        return report.Reencrypted;
    }

    public Task<MfaRotationReport> ReencryptAsync(CancellationToken cancellationToken) =>
        runner.ForPlatformAsync(
            async (services, ct) =>
            {
                var db = services.GetRequiredService<AppDbContext>();
                var prefix = protector.CurrentPrefix;
                var reencrypted = 0;
                var unreadable = 0;
                var attempted = new List<MfaCredentialId>();
                while (true)
                {
                    var batch = await db.Set<MfaCredential>().AsNoTracking()
                        .Where(c => !c.SecretCiphertext.StartsWith(prefix) && !attempted.Contains(c.Id))
                        .OrderBy(c => c.Id)
                        .Take(BatchSize)
                        .Select(c => new { Id = c.Id.Value, UserId = c.UserId.Value, c.SecretCiphertext })
                        .ToListAsync(ct);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    foreach (var row in batch)
                    {
                        byte[] secret;
                        try
                        {
                            secret = protector.Unprotect(row.UserId, row.SecretCiphertext);
                        }
                        catch (CryptographicException)
                        {
                            unreadable++;
                            attempted.Add(MfaCredentialId.From(row.Id)); // left alone: only these stay out of the next batch
                            continue;
                        }

                        var replacement = protector.Protect(row.UserId, secret);
                        var id = MfaCredentialId.From(row.Id);
                        var old = row.SecretCiphertext;
                        var changed = await db.Set<MfaCredential>()
                            .Where(c => c.Id == id && c.SecretCiphertext == old)
                            .ExecuteUpdateAsync(set => set.SetProperty(c => c.SecretCiphertext, replacement), ct);
                        reencrypted += changed;
                        if (changed == 0)
                        {
                            attempted.Add(id); // changed under us: not retried in this run
                        }
                    }
                }

                var remaining = await db.Set<MfaCredential>().CountAsync(c => !c.SecretCiphertext.StartsWith(prefix), ct);
                return new MfaRotationReport(reencrypted, unreadable, remaining);
            },
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "MFA secrets: {Reencrypted} moved to the current key, {Remaining} still under other keys")]
    private static partial void LogReport(ILogger logger, int reencrypted, int remaining);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MFA secrets: {Count} cannot be decrypted with any configured key; their owners need a reset by a Platform Admin")]
    private static partial void LogUnreadable(ILogger logger, int count);
}
