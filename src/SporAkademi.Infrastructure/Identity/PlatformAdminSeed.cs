using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>
/// P1-12: creates the first Platform Admin at startup when none exists and <c>PlatformAdmin:Email</c> and
/// <c>PlatformAdmin:InitialPassword</c> are configured. The password must be changed at first login.
/// Without configuration it does nothing; with only one setting (or a placeholder password) it warns;
/// an invalid e-mail or weak password stops the startup. Instances starting together serialize on an
/// advisory lock, so at most one Platform Admin is ever seeded. Logs never contain the e-mail or password.
/// </summary>
internal sealed partial class PlatformAdminSeed(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PlatformAdminSeed> logger) : IHostedService
{
    public const string Section = "PlatformAdmin";
    public const string EmailSetting = Section + ":Email";
    public const string PasswordSetting = Section + ":InitialPassword";

    /// <summary>Transaction-scoped advisory lock key for the seed ("SAPASEED" as bytes).</summary>
    private const long LockKey = 0x5341_5041_5345_4544;
    private const string UniqueViolation = "23505";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration[EmailSetting];
        var password = configuration[PasswordSetting];
        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var hasPassword = !string.IsNullOrWhiteSpace(password);
        if (!hasEmail && !hasPassword)
        {
            return;
        }

        if (!hasEmail || !hasPassword)
        {
            LogPartialConfiguration(logger, hasEmail ? PasswordSetting : EmailSetting);
            return;
        }

        if (password!.StartsWith("change-me", StringComparison.OrdinalIgnoreCase))
        {
            LogPlaceholderPassword(logger, PasswordSetting);
            return;
        }

        try
        {
            switch (await SeedAsync(email!, password, cancellationToken))
            {
                case SeedOutcome.Created:
                    LogSeeded(logger);
                    break;
                case SeedOutcome.EmailInUse:
                    LogEmailInUse(logger, EmailSetting);
                    break;
            }
        }
        catch (DbException exception)
        {
            // The API still starts without the database (readiness reports it); the seed runs at the next start.
            LogDatabaseUnavailable(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task<SeedOutcome> SeedAsync(string email, string password, CancellationToken cancellationToken)
    {
        var address = Email.Create(email);
        if (address.IsFailure)
        {
            throw new InvalidOperationException($"{EmailSetting} is not a valid e-mail address.");
        }

        if (!PasswordPolicy.IsStrongEnough(password))
        {
            throw new InvalidOperationException($"{PasswordSetting} does not meet the password policy (NFR-SEC-04).");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Another instance seeding at the same time waits here and then sees its admin.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);

            if (await db.Set<User>().AnyAsync(u => u.Role == UserRole.PlatformAdmin, cancellationToken))
            {
                return SeedOutcome.AlreadySeeded;
            }

            if (await db.Set<User>().AnyAsync(u => u.Email == address.Value, cancellationToken))
            {
                return SeedOutcome.EmailInUse;
            }

            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Add(User.Create(null, address.Value, hasher.Hash(password), UserRole.PlatformAdmin, "Platform", "Admin", null).Value);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SeedOutcome.Created;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // A writer outside the lock took the e-mail first: someone else seeded or uses it.
            return SeedOutcome.AlreadySeeded;
        }
    }

    internal enum SeedOutcome
    {
        Created,
        AlreadySeeded,
        EmailInUse,
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The first Platform Admin was created; the password must be changed at first login")]
    private static partial void LogSeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform Admin seed skipped: {Setting} is not configured")]
    private static partial void LogPartialConfiguration(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform Admin seed skipped: {Setting} is still the placeholder value")]
    private static partial void LogPlaceholderPassword(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform Admin seed skipped: the address in {Setting} already belongs to another account")]
    private static partial void LogEmailInUse(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Platform Admin seed was skipped: the database is not reachable")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);
}
