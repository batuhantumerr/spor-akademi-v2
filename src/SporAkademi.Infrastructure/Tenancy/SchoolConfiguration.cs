using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

internal sealed class SchoolConfiguration : IEntityTypeConfiguration<School>
{
    public void Configure(EntityTypeBuilder<School> builder)
    {
        builder.ToTable("schools", Schemas.Tenancy);
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Name).HasMaxLength(School.NameMaxLength);
        builder.HasIndex(s => s.Slug).IsUnique();
        builder.Property(s => s.TimeZone).HasMaxLength(School.TimeZoneMaxLength);

        builder.ComplexProperty(s => s.RetentionPolicy, policy =>
        {
            policy.Property(p => p.PersonalDataMonths).HasColumnName("retention_personal_data_months");
            policy.Property(p => p.PaymentYears).HasColumnName("retention_payment_years");
        });

        builder.Ignore(s => s.DomainEvents);
    }
}
