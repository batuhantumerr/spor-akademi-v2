using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>P3-08: a recorded payment (by hand or online) becomes an integration message in the same transaction (DEC-396).</summary>
internal sealed class PublishPaymentRecorded(IOutbox outbox) : IDomainEventConsumer<PaymentRecorded>
{
    public Task<Result> Handle(PaymentRecorded domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(
            new PaymentRecordedMessage(domainEvent.PaymentId.Value, domainEvent.StudentId.Value, domainEvent.GroupId.Value),
            domainEvent.TenantId.Value);
        return Task.FromResult(Result.Success());
    }
}
