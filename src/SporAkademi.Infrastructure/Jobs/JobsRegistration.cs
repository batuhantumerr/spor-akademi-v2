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
            .UseServerFiltersOnce()
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
        services.AddScoped<OnlinePaymentReconcileJob>();
        services.AddScoped<StudentAnonymizationJob>();
        services.AddScoped<DuesReminderJob>();
        services.AddScoped<BillingPeriodCloseJob>();
        services.AddScoped<Audit.AuditRetentionJob>();
        services.AddSingleton<JobsDashboardSessions>();
        services.AddSingleton<PanelSessionRevocationInterceptor>();
        services.AddScoped<IJobsDashboardAccess, JobsDashboardAccess>();
        return services;
    }

    private static readonly Lock FilterGate = new();

    /// <summary>
    /// Hangfire keeps job filters in a process-wide static list (<see cref="GlobalJobFilters.Filters"/>), and the
    /// configuration callback runs once per host. Adding new instances every time stacked one more filter per host,
    /// so a test run that builds hundreds of hosts eventually overflowed the stack in the filter chain. Each filter
    /// type is added only when the list does not already hold one.
    /// </summary>
    private static IGlobalConfiguration UseServerFiltersOnce(this IGlobalConfiguration config)
    {
        lock (FilterGate)
        {
            if (!GlobalJobFilters.Filters.Any(f => f.Instance is FailedJobExceptionFilter))
            {
                config.UseFilter(new FailedJobExceptionFilter());
            }

            if (!GlobalJobFilters.Filters.Any(f => f.Instance is JobActivityFilter))
            {
                config.UseFilter(new JobActivityFilter());
            }
        }

        return config;
    }
}
