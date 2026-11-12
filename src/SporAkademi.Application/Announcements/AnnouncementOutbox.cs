using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Announcements.Contracts;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Announcements;

/// <summary>P3-08: a published announcement becomes an integration message in the same transaction (DEC-396).</summary>
internal sealed class PublishAnnouncementPublished(IOutbox outbox) : IDomainEventConsumer<AnnouncementPublished>
{
    public Task<Result> Handle(AnnouncementPublished domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(new AnnouncementPublishedMessage(domainEvent.AnnouncementId.Value, domainEvent.GroupId.Value), domainEvent.TenantId.Value);
        return Task.FromResult(Result.Success());
    }
}
