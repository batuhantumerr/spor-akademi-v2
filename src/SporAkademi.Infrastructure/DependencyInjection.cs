using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Read lazily below, but still fail at startup when it is missing.
        services.AddOptions<DatabaseConfigurationCheck>()
            .Validate<IConfiguration>(
                (_, config) => !string.IsNullOrWhiteSpace(config.GetConnectionString("App")),
                "Connection string 'App' is not configured.")
            .ValidateOnStart();

        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AuditingInterceptor>();
        // The connection string is read when the first context is built, not at registration,
        // so hosts and test factories can supply it through any configuration source.
        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IConfiguration>().GetConnectionString("App")
                    ?? throw new InvalidOperationException("Connection string 'App' is not configured."), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(provider.GetRequiredService<AuditingInterceptor>()));

        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

        return services;
    }
}

internal sealed class DatabaseConfigurationCheck;
