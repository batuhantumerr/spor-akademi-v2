using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Infrastructure.Identity;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.Infrastructure.Tenancy;

namespace SporAkademi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<TenantWriteGuardInterceptor>();
        // The connection string is read when the first context is built, not at registration,
        // so hosts and test factories can supply it through any configuration source.
        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IConfiguration>().GetConnectionString("App")
                    ?? throw new InvalidOperationException("Connection string 'App' is not configured."), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                provider.GetRequiredService<TenantWriteGuardInterceptor>(),
                provider.GetRequiredService<AuditingInterceptor>()));

        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<ITenancyModule, TenancyModule>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

        return services;
    }
}
