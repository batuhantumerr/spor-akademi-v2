using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Abstractions.Storage;
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
using SporAkademi.Application.Students.Documents;
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
using SporAkademi.Infrastructure.Notifications;
using SporAkademi.Infrastructure.Organization;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.Infrastructure.Storage;
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
        services.AddScoped<IStudentDocumentRepository, StudentDocumentRepository>();
        services.AddDocumentStorage(configuration);
        services.AddScoped<IStudentReminderContacts, StudentReminderContacts>();
        services.AddScoped<IDuesReminderRepository, DuesReminderRepository>();
        services.AddSingleton<IPasswordTokenFactory, PasswordTokenFactory>();
        services.AddOptions<DuesReminderSettings>()
            .BindConfiguration(DuesReminderSettings.Section)
            .Validate(o => o.IsValid(), "The Billing:DuesReminders configuration section has an invalid value.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DuesReminderSettings>>().Value);
        services.AddEmail();

        services.AddOutbox(configuration);
        services.AddJobs();
        services.AddSingleton(new HeartbeatClock(TimeProvider.System));

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
            .AddCheck<DocumentStorageHealthCheck>("storage", HealthStatus.Degraded, tags: ["ready"])
            // A broken outbox degrades readiness but never fails it; the database check covers outages.
            .AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Degraded, tags: ["ready"])
            // A stopped job server degrades readiness too (DEC-335).
            .AddCheck<JobsHealthCheck>("jobs", failureStatus: HealthStatus.Degraded, tags: ["ready"]);

        return services;
    }
}

internal sealed class DatabaseConfigurationCheck;

internal static class DocumentStorageRegistration
{
    /// <summary>
    /// DEC-326: validated when first used, not at start, so hosts without document storage (CI smoke test, demo
    /// seeder) still start; the readiness check reports the store as Degraded until it is configured.
    /// </summary>
    public static IServiceCollection AddDocumentStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DocumentStorageOptions>()
            .Bind(configuration.GetSection(DocumentStorageOptions.Section))
            .Validate(o => DocumentStorageOptions.IsAbsoluteHttpUrl(o.ServiceUrl), "Storage:ServiceUrl must be an http(s) URL.")
            .Validate(o => o.PublicUrl is null || DocumentStorageOptions.IsAbsoluteHttpUrl(o.PublicUrl), "Storage:PublicUrl must be an http(s) URL.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AccessKey) && !string.IsNullOrWhiteSpace(o.SecretKey), "Storage:AccessKey and Storage:SecretKey are required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "Storage:Bucket is required.")
            .Validate(o => o.DownloadLinkLifetime >= TimeSpan.FromSeconds(1) && o.DownloadLinkLifetime <= TimeSpan.FromMinutes(15), "Storage:DownloadLinkLifetime must be between 1 second and 15 minutes.");
        services.AddSingleton(new StorageClock(TimeProvider.System));
        services.AddSingleton<S3Clients>();
        services.AddSingleton<IDocumentStorage, S3DocumentStorage>();
        return services;
    }
}

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
