using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Announcements.Contracts;

/// <summary>An announcement was published (P3-08, DEC-397). Ids only (NFR-PRV-03); handlers read what they need.</summary>
[OutboxMessage("announcements.announcement-published.v1")]
public sealed record AnnouncementPublishedMessage(Guid AnnouncementId, Guid GroupId) : IOutboxMessage;
