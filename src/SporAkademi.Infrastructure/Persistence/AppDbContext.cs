using Microsoft.EntityFrameworkCore;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<Slug>().HaveConversion<SlugConverter>().HaveMaxLength(Slug.MaxLength);
        configurationBuilder.Properties<Email>().HaveConversion<EmailConverter>().HaveMaxLength(Email.MaxLength);
        configurationBuilder.Properties<PhoneNumber>().HaveConversion<PhoneNumberConverter>().HaveMaxLength(PhoneNumber.MaxLength);
        configurationBuilder.Properties<YearMonth>().HaveConversion<YearMonthConverter>().HaveMaxLength(7);

        // Multi-field value objects are always complex types, never entities.
        configurationBuilder.ComplexProperties<RetentionPolicy>();

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
    }

    private static bool IsEntityIdInterface(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEntityId<>);
}
