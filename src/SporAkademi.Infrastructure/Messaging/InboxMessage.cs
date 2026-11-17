using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Messaging;

/// <summary>
/// A row of <c>outbox.inbox</c> (P5-03): this consumer has handled this message. The broker delivers at least once, so a
/// consumer inserts its row in the transaction of its own work and skips the message when the row already exists
/// (idempotent consumer). A platform table like <c>outbox.messages</c>; written by raw SQL (<c>ON CONFLICT DO NOTHING</c>).
/// </summary>
internal sealed class InboxMessage
{
    public const int ConsumerMaxLength = 300;

    public string Consumer { get; private set; } = string.Empty;

    public Guid MessageId { get; private set; }

    public Guid? TenantId { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox", Schemas.Outbox);
        builder.HasKey(m => new { m.Consumer, m.MessageId });
        builder.Property(m => m.Consumer).HasMaxLength(InboxMessage.ConsumerMaxLength);
        builder.HasIndex(m => m.ProcessedAt).HasDatabaseName("ix_inbox_processed_at");
    }
}
