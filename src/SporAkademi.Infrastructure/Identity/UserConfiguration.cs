using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", Schemas.Identity);
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        // E-mails are normalized to lower case by the value object, so a plain unique index is platform-wide uniqueness.
        builder.HasIndex(u => u.Email).IsUnique();
        builder.Property(u => u.PasswordHash).HasMaxLength(User.PasswordHashMaxLength);
        builder.Property(u => u.FirstName).HasMaxLength(User.NameMaxLength);
        builder.Property(u => u.LastName).HasMaxLength(User.NameMaxLength);

        builder.HasOne<School>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(u => u.RefreshTokens).WithOne().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.RefreshTokens).HasField("_refreshTokens").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(u => u.DomainEvents);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens", Schemas.Identity);
        builder.HasKey(t => t.Id);
        // Client-generated ids: a token added to the collection is new, never an update.
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash).HasMaxLength(RefreshToken.TokenHashLength).IsFixedLength();
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Two concurrent refreshes with the same token: only one may rotate it, the other gets a conflict.
        builder.Property<uint>(ConcurrencyToken.PropertyName)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
