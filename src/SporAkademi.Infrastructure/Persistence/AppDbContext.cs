using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser) : DbContext(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly SchoolId NoTenant = SchoolId.From(Guid.Empty);

    /// <summary>Read by the tenant query filters for every query; an empty id matches no rows.</summary>
    internal SchoolId CurrentTenantId => currentUser.TenantId is { } tenant ? SchoolId.From(tenant) : NoTenant;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<Slug>().HaveConversion<SlugConverter>().HaveMaxLength(Slug.MaxLength);
        configurationBuilder.Properties<Email>().HaveConversion<EmailConverter>().HaveMaxLength(Email.MaxLength);
        configurationBuilder.Properties<PhoneNumber>().HaveConversion<PhoneNumberConverter>().HaveMaxLength(PhoneNumber.MaxLength);
        configurationBuilder.Properties<YearMonth>().HaveConversion<YearMonthConverter>().HaveMaxLength(7);

        // Multi-field value objects are always complex types, never entities.
        configurationBuilder.ComplexProperties<RetentionPolicy>();
        configurationBuilder.ComplexProperties<Address>();
        configurationBuilder.ComplexProperties<EmergencyContact>();
        configurationBuilder.ComplexProperties<BirthYearRange>();
        configurationBuilder.ComplexProperties<TimeRange>();
        configurationBuilder.ComplexProperties<ScheduleSlot>();
        configurationBuilder.ComplexProperties<Money>();

        // Every strongly typed id is stored as its Guid; every domain enum as its name.
        foreach (var type in typeof(IEntityId<>).Assembly.GetTypes())
        {
            if (type.IsValueType && type.GetInterfaces().Any(IsEntityIdInterface))
            {
                configurationBuilder.Properties(type).HaveConversion(typeof(EntityIdConverter<>).MakeGenericType(type));
            }
            else if (type.IsEnum && type.IsVisible)
            {
                configurationBuilder.Properties(type).HaveConversion<string>().HaveMaxLength(32);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.AddAuditColumns();
        modelBuilder.AddConcurrencyTokens();

        // Layer 1 of ADR-0003: declared here so each filter reads this.CurrentTenantId, which EF
        // re-evaluates per context instance. Without a tenant the filter matches nothing.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => t.BaseType is null && typeof(ITenantScoped).IsAssignableFrom(t.ClrType)).ToList())
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == CurrentTenantId);

    private static bool IsEntityIdInterface(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEntityId<>);
}
