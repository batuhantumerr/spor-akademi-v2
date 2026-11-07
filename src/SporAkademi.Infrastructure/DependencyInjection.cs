using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Announcements;
using SporAkademi.Application.Attendance;
using SporAkademi.Application.Billing;
using SporAkademi.Application.Enrollments;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Organization;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Application.Students;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Infrastructure.Announcements;
using SporAkademi.Infrastructure.Attendance;
using SporAkademi.Infrastructure.Billing;
using SporAkademi.Infrastructure.Caching;
using SporAkademi.Infrastructure.Enrollments;
using SporAkademi.Infrastructure.Groups;
using SporAkademi.Infrastructure.Identity;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Organization;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.Infrastructure.Students;
using SporAkademi.Infrastructure.Tenancy;

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
        // Background work (outbox handlers, P2-F2 jobs) acts for a school without an HTTP request.
        services.AddScoped<BackgroundTenantContext>();
        services.Decorate<ICurrentUser, BackgroundAwareCurrentUser>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AuditingInterceptor>();
        services.AddScoped<TenantWriteGuardInterceptor>();
        services.AddTenantCache();
        // The connection string is read when the first context is built, not at registration,
        // so hosts and test factories can supply it through any configuration source.
        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(
                provider.GetRequiredService<IConfiguration>().GetConnectionString("App")
                    ?? throw new InvalidOperationException("Connection string 'App' is not configured."), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                provider.GetRequiredService<TenantWriteGuardInterceptor>(),
                provider.GetRequiredService<AuditingInterceptor>(),
                provider.GetRequiredService<CacheInvalidationInterceptor>(),
                provider.GetRequiredService<PanelSessionRevocationInterceptor>()));

        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<ITenancyModule, TenancyModule>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IRefreshTokenFactory, RefreshTokenFactory>();
        services.AddScoped<ISchoolRepository, SchoolRepository>();
        services.AddScoped<ICoachDirectory, CoachDirectory>();
        services.AddSingleton<PlatformAdminSeed>();
        services.AddHostedService(provider => provider.GetRequiredService<PlatformAdminSeed>());
        services.AddScoped<ISportRepository, SportRepository>();
        services.AddScoped<IFacilityRepository, FacilityRepository>();
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<ICoachGroups, CoachGroups>();
        services.AddScoped<IAnnouncementRepository, AnnouncementRepository>();
        services.AddScoped<IGroupLocks, GroupLocks>();
        services.AddScoped<IGroupUsage, GroupUsage>();
        services.AddScoped<IGroupDirectory, GroupDirectory>();
        services.AddScoped<IGroupFeeSchedule, GroupFeeSchedule>();
        services.AddScoped<IEnrollmentDirectory, EnrollmentDirectory>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IEnrollmentRepository, EnrollmentRepository>();
        services.AddScoped<IEnrollmentLocks, EnrollmentLocks>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<Application.Attendance.Contracts.IAttendanceSummary, Attendance.AttendanceSummary>();
        services.AddScoped<Application.Dashboard.IDashboardReadModel, Dashboard.DashboardReadModel>();
        services.AddScoped<ISessionLocks, SessionLocks>();
        services.AddScoped<IStudentDirectory, StudentDirectory>();
        services.AddScoped<IStudentRepository, StudentRepository>();
        services.AddScoped<IGuardianRepository, GuardianRepository>();
        services.AddScoped<IAnonymizationRunRepository, AnonymizationRunRepository>();

        services.AddOutbox(configuration);
        services.AddJobs();
        services.AddSingleton(new HeartbeatClock(TimeProvider.System));

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
            // A broken outbox degrades readiness but never fails it; the database check covers outages.
            .AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Degraded, tags: ["ready"])
            // A stopped job server degrades readiness too (DEC-335).
            .AddCheck<JobsHealthCheck>("jobs", failureStatus: HealthStatus.Degraded, tags: ["ready"]);

        return services;
    }
}

internal sealed class DatabaseConfigurationCheck;

internal static class OutboxRegistration
{
    /// <summary>P2-F3: the outbox, its message types from the application layer and the polling worker (DEC-300 – DEC-303).</summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.Section))
            .Validate(o => o.IsValid(), "The Outbox configuration section has an invalid value.")
            .ValidateOnStart();

        foreach (var type in typeof(IOutboxMessage).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IOutboxMessage).IsAssignableFrom(t)))
        {
            services.AddOutboxMessageType(type);
        }

        services.AddSingleton<OutboxMessageTypes>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddSingleton<OutboxProcessor>();

        // Always registered; it returns at once unless Outbox:Scheduler is Worker (read at start, so test hosts can set it).
        services.AddHostedService<OutboxWorker>();

        return services;
    }

    public static IServiceCollection AddOutboxMessageType(this IServiceCollection services, Type messageType)
    {
        services.AddSingleton(new OutboxMessageRegistration(messageType));
        return services;
    }
}
