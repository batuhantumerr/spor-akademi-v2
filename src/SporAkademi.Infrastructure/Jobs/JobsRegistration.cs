using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SporAkademi.Infrastructure.Jobs;

internal static class JobsRegistration
{
    /// <summary>
    /// P2-F2 (DEC-330, DEC-331): Hangfire with PostgreSQL storage in the <c>hangfire</c> schema, connected as the
    /// app role and never preparing the schema (the migrator owns it); the in-process server and the recurring jobs.
    /// </summary>
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        services.AddOptions<JobsOptions>()
            .BindConfiguration(JobsOptions.Section)
            .Validate(o => o.IsValid(), "The Jobs configuration section has an invalid value.")
            .ValidateOnStart();

        // The connection string and polling settings are read when the storage is first resolved, not here, so test
        // hosts can supply them through any configuration source.
        services.AddHangfire((provider, config) => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseFilter(new FailedJobExceptionFilter())
            .UsePostgreSqlStorage(
                storage => storage.UseNpgsqlConnection(
                    provider.GetRequiredService<IConfiguration>().GetConnectionString("App")
                        ?? throw new InvalidOperationException("Connection string 'App' is not configured.")),
                new PostgreSqlStorageOptions
                {
                    SchemaName = HangfireSchema.Name,
                    PrepareSchemaIfNecessary = false,
                    QueuePollInterval = provider.GetRequiredService<IOptions<JobsOptions>>().Value.QueuePollInterval,
                }));

        services.AddSingleton<JobServerIdentity>();
        services.AddSingleton<JobsMonitoringSnapshot>();
        services.AddHostedService<JobServerHost>();
        services.AddSingleton<TenantJobRunner>();
        services.AddScoped<OutboxJobs>();
        services.AddScoped<RefreshTokenCleanupJob>();
        services.AddSingleton<JobsDashboardSessions>();
        services.AddSingleton<PanelSessionRevocationInterceptor>();
        services.AddScoped<IJobsDashboardAccess, JobsDashboardAccess>();
        return services;
    }
}
