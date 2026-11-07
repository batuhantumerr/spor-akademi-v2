using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// A row of <c>outbox.messages</c> (P2-F3). Pending: neither processed nor dead-lettered. Not tenant-scoped (the
/// processor reads every school's messages); <see cref="TenantId"/> sets the tenant while the handlers run.
/// </summary>
internal sealed class OutboxMessage
{
    public const int TypeMaxLength = 200;
    public const int LastErrorMaxLength = 500;

    public OutboxMessage(Guid id, string type, string payload, Guid? tenantId, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        TenantId = tenantId;
        OccurredAt = occurredAt;
        NextAttemptAt = occurredAt;
    }

    public Guid Id { get; private set; }

    /// <summary>The registered message name (<c>module.event.vN</c>), never a CLR type name.</summary>
    public string Type { get; private set; }

    public string Payload { get; private set; }

    public Guid? TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Failed deliveries so far.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset? DeadLetteredAt { get; private set; }

    /// <summary>A code or exception type of the last failure; never an exception message (it may hold personal data).</summary>
    public string? LastError { get; private set; }

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        LastError = null;
    }

    /// <summary>Counts a failed attempt; after <paramref name="maxAttempts"/> the message is dead-lettered and no longer retried.</summary>
    public void RecordFailure(string error, DateTimeOffset now, int maxAttempts, TimeSpan retryDelay)
    {
        Attempts++;
        LastError = error.Length <= LastErrorMaxLength ? error : error[..LastErrorMaxLength];
        if (Attempts >= maxAttempts)
        {
            DeadLetteredAt = now;
        }
        else
        {
            NextAttemptAt = now + retryDelay;
        }
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("messages", Schemas.Outbox);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // The processor's claim query: due pending messages in order.
        builder.HasIndex(m => new { m.NextAttemptAt, m.Id })
            .HasDatabaseName("ix_messages_pending")
            .HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL");

        // Cleanup of old processed messages and the health check's dead-letter count.
        builder.HasIndex(m => m.ProcessedAt)
            .HasDatabaseName("ix_messages_processed_at")
            .HasFilter("processed_at IS NOT NULL");
        builder.HasIndex(m => m.DeadLetteredAt)
            .HasDatabaseName("ix_messages_dead_lettered_at")
            .HasFilter("dead_lettered_at IS NOT NULL");
    }
}
