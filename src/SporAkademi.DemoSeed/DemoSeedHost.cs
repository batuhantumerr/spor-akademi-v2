using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SporAkademi.Application;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure;

namespace SporAkademi.DemoSeed;

/// <summary>
/// The application and infrastructure layers wired like the API, with a controllable actor and clock instead of
/// the HTTP request: every change goes through the same command handlers, decorators and unit of work.
/// </summary>
internal static class DemoSeedHost
{
    public static IHost Create(string[] args, IDictionary<string, string?>? settings = null, string? environment = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, EnvironmentName = environment });
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Registered before the layers, which only add fallbacks for these.
        builder.Services.AddSingleton<SeedClock>();
        builder.Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<SeedClock>());
        builder.Services.AddSingleton<SeedActor>();
        builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<SeedActor>());
        builder.Services.AddSingleton<IAccessTokenIssuer, NoAccessTokens>();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddScoped<DemoSeeder>();

        return builder.Build();
    }
}

/// <summary>Who the seeder acts as: the same role and school rules apply as for a real request.</summary>
internal sealed class SeedActor : ICurrentUser
{
    public bool IsAuthenticated => UserId is not null;

    public Guid? UserId { get; private set; }

    public Guid? TenantId { get; private set; }

    public Role? Role { get; private set; }

    public void Act(Role role, Guid userId, Guid? tenantId)
    {
        Role = role;
        UserId = userId;
        TenantId = tenantId;
    }
}

/// <summary>The seeder replays a school's last months day by day, so time rules ("not in the past", "not in the future") hold.</summary>
internal sealed class SeedClock : TimeProvider
{
    private DateTimeOffset _now = System.GetUtcNow();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset utc) => _now = utc;
}

/// <summary>Password changes return a session; the seeder has no use for access tokens.</summary>
internal sealed class NoAccessTokens : IAccessTokenIssuer
{
    public AccessToken Issue(User user, DateTimeOffset now) => new(string.Empty, now);
}
