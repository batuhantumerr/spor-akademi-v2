using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Persistence;

internal sealed class EntityIdConverter<TId>() : ValueConverter<TId, Guid>(id => id.Value, value => FromGuid(value))
    where TId : struct, IEntityId<TId>
{
    // Expression trees cannot call static abstract interface members directly.
    private static TId FromGuid(Guid value) => TId.From(value);
}

internal static class AuditColumns
{
    public const string CreatedAt = "CreatedAt";
    public const string CreatedBy = "CreatedBy";
    public const string UpdatedAt = "UpdatedAt";
    public const string UpdatedBy = "UpdatedBy";
}

internal static class ModelConventions
{
    /// <summary>Shadow audit columns on every table, filled by <see cref="AuditingInterceptor"/>.</summary>
    public static void AddAuditColumns(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.BaseType is null).ToList())
        {
            var entity = modelBuilder.Entity(entityType.ClrType);
            entity.Property<DateTimeOffset>(AuditColumns.CreatedAt);
            entity.Property<Guid?>(AuditColumns.CreatedBy);
            entity.Property<DateTimeOffset>(AuditColumns.UpdatedAt);
            entity.Property<Guid?>(AuditColumns.UpdatedBy);
        }
    }

    /// <summary>PostgreSQL <c>xmin</c> as the optimistic concurrency token of every aggregate root.</summary>
    public static void AddConcurrencyTokens(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => t.BaseType is null && typeof(IAggregateRoot).IsAssignableFrom(t.ClrType)).ToList())
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property<uint>(ConcurrencyToken.PropertyName)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }
    }
}

public static class ConcurrencyToken
{
    public const string PropertyName = "Version";
}

internal sealed class SlugConverter() : ValueConverter<Slug, string>(slug => slug.Value, value => FromDatabase(value))
{
    private static Slug FromDatabase(string value) => Slug.Create(value).Value;
}

internal sealed class EmailConverter() : ValueConverter<Email, string>(email => email.Value, value => FromDatabase(value))
{
    private static Email FromDatabase(string value) => Email.Create(value).Value;
}

internal sealed class PhoneNumberConverter() : ValueConverter<PhoneNumber, string>(phone => phone.Value, value => FromDatabase(value))
{
    private static PhoneNumber FromDatabase(string value) => PhoneNumber.Create(value).Value;
}

/// <summary>Stored as <c>YYYY-MM</c> text, which sorts and compares correctly in SQL.</summary>
internal sealed class YearMonthConverter() : ValueConverter<YearMonth, string>(period => period.ToString(), value => FromDatabase(value))
{
    private static YearMonth FromDatabase(string value) => YearMonth.Parse(value).Value;
}
