using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Application.Abstractions.Auditing;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Audit;

/// <summary>
/// One row of the personal data audit log (P4-05, DEC-530): who, when, what, which record, which fields. Never a value:
/// <see cref="Fields"/> holds names and <see cref="Reason"/> a fixed code (DEC-533). Rows are only ever added; the
/// application role cannot change or delete them (DEC-535), and the retention function removes them after two years
/// (DEC-536).
/// </summary>
internal sealed class AuditEntry : ITenantScoped
{
    public AuditEntry(
        SchoolId tenantId,
        DateTimeOffset occurredAt,
        Guid? userId,
        string? userRole,
        AuditAction action,
        AuditEntityType entityType,
        Guid? entityId,
        IReadOnlyCollection<string> fields,
        string? reason,
        string? correlationId)
    {
        Id = Guid.CreateVersion7(occurredAt);
        TenantId = tenantId;
        OccurredAt = occurredAt;
        UserId = userId;
        UserRole = userRole;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Fields = [.. fields.Order(StringComparer.Ordinal)];
        Reason = reason;
        CorrelationId = correlationId;
    }

    // For EF Core materialization only.
    private AuditEntry()
    {
        Fields = [];
    }

    public Guid Id { get; private set; }

    public SchoolId TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary><c>null</c> for work without a signed-in user (the nightly jobs, a password set from an e-mail link).</summary>
    public Guid? UserId { get; private set; }

    public string? UserRole { get; private set; }

    public AuditAction Action { get; private set; }

    public AuditEntityType EntityType { get; private set; }

    /// <summary><c>null</c> for a list query, which names no single record.</summary>
    public Guid? EntityId { get; private set; }

    public string[] Fields { get; private set; }

    public string? Reason { get; private set; }

    public string? CorrelationId { get; private set; }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public const string Table = "audit_entries";

    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable(Table, Schemas.Audit);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.HasOne<School>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(e => e.UserRole).HasMaxLength(32);
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.EntityType).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Reason).HasMaxLength(32);
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        // The list screen: newest first, optionally per user or record type; the retention function: by age.
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt, e.Id });
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.EntityType, e.OccurredAt });
        builder.HasIndex(e => e.OccurredAt);
    }
}
