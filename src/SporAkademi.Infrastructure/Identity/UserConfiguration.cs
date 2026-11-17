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
        builder.HasMany(u => u.PasswordTokens).WithOne().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.PasswordTokens).HasField("_passwordTokens").UsePropertyAccessMode(PropertyAccessMode.Field);

        // P5-01: the second factor. The credential is one row per user (unique), the codes and the challenge are children.
        builder.Property(u => u.MfaEnabled).HasDefaultValue(false);
        builder.HasOne(u => u.MfaCredential).WithOne().HasForeignKey<MfaCredential>(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.MfaCredential).HasField("_mfaCredential").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(u => u.RecoveryCodes).WithOne().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.RecoveryCodes).HasField("_recoveryCodes").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(u => u.MfaChallenges).WithOne().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.MfaChallenges).HasField("_mfaChallenges").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(u => u.DomainEvents);
    }
}

/// <summary>The TOTP secret (P5-01, DEC-602): encrypted, one per user. Two concurrent code checks cannot both accept the same step.</summary>
internal sealed class MfaCredentialConfiguration : IEntityTypeConfiguration<MfaCredential>
{
    public void Configure(EntityTypeBuilder<MfaCredential> builder)
    {
        builder.ToTable("mfa_credentials", Schemas.Identity);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => c.UserId).IsUnique();
        builder.Property(c => c.SecretCiphertext).HasMaxLength(MfaCredential.CiphertextMaxLength);

        builder.Property<uint>(ConcurrencyToken.PropertyName)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

/// <summary>Recovery codes (P5-01, DEC-603): hash only; using one twice at once conflicts.</summary>
internal sealed class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        builder.ToTable("mfa_recovery_codes", Schemas.Identity);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => c.UserId);
        builder.Property(c => c.CodeHash).HasMaxLength(MfaRecoveryCode.HashLength).IsFixedLength();

        builder.Property<uint>(ConcurrencyToken.PropertyName)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

/// <summary>The second step of a login (P5-01, DEC-601): hash only. Two concurrent answers cannot both use it up or both count.</summary>
internal sealed class MfaChallengeConfiguration : IEntityTypeConfiguration<MfaChallenge>
{
    public void Configure(EntityTypeBuilder<MfaChallenge> builder)
    {
        builder.ToTable("mfa_challenges", Schemas.Identity);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => c.UserId);
        builder.Property(c => c.TokenHash).HasMaxLength(MfaChallenge.TokenHashLength).IsFixedLength();
        builder.HasIndex(c => c.TokenHash).IsUnique();
        builder.HasIndex(c => c.ExpiresAt);

        builder.Property<uint>(ConcurrencyToken.PropertyName)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
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

/// <summary>Set-password tokens (P2-F4, DEC-352): hash only, at most one per user (the aggregate replaces it).</summary>
internal sealed class PasswordTokenConfiguration : IEntityTypeConfiguration<PasswordToken>
{
    public void Configure(EntityTypeBuilder<PasswordToken> builder)
    {
        builder.ToTable("password_tokens", Schemas.Identity);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash).HasMaxLength(PasswordToken.TokenHashLength).IsFixedLength();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.ExpiresAt);

        // Two concurrent confirmations with the same link: only one may use it, the other gets a conflict.
        builder.Property<uint>(ConcurrencyToken.PropertyName)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
