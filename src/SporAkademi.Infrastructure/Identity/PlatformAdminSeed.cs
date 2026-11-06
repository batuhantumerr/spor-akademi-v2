using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>
/// P1-12: creates the first Platform Admin at startup when none exists and <c>PlatformAdmin:Email</c> and
/// <c>PlatformAdmin:InitialPassword</c> are configured. The password must be changed at first login.
/// Idempotent; a no-op without configuration. Logs only that it happened, never the e-mail or password.
/// </summary>
internal sealed partial class PlatformAdminSeed(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PlatformAdminSeed> logger) : IHostedService
{
    public const string Section = "PlatformAdmin";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration[$"{Section}:Email"];
        var password = configuration[$"{Section}:InitialPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        try
        {
            if (await SeedAsync(email, password, cancellationToken))
            {
                LogSeeded(logger);
            }
        }
        catch (DbException exception)
        {
            // The API still starts without the database (readiness reports it); the seed runs at the next start.
            LogDatabaseUnavailable(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task<bool> SeedAsync(string email, string password, CancellationToken cancellationToken)
    {
        var address = Email.Create(email);
        if (address.IsFailure)
        {
            throw new InvalidOperationException($"{Section}:Email is not a valid e-mail address.");
        }

        if (!PasswordPolicy.IsStrongEnough(password))
        {
            throw new InvalidOperationException($"{Section}:InitialPassword does not meet the password policy (NFR-SEC-04).");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Set<User>().AnyAsync(u => u.Role == UserRole.PlatformAdmin, cancellationToken))
        {
            return false;
        }

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var admin = User.Create(null, address.Value, hasher.Hash(password), UserRole.PlatformAdmin, "Platform", "Admin", null).Value;
        db.Add(admin);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The first Platform Admin was created; the password must be changed at first login")]
    private static partial void LogSeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Platform Admin seed was skipped: the database is not reachable")]
    private static partial void LogDatabaseUnavailable(ILogger logger, Exception exception);
}
