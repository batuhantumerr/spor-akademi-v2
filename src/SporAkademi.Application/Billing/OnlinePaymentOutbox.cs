using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>P2-F1: a committed online payment success becomes an integration message in the same transaction.</summary>
internal sealed class PublishOnlinePaymentSucceeded(IOutbox outbox) : IDomainEventConsumer<OnlinePaymentSucceeded>
{
    public Task<Result> Handle(OnlinePaymentSucceeded domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(new OnlinePaymentSucceededMessage(domainEvent.OnlinePaymentId.Value, domainEvent.PaymentId.Value), domainEvent.TenantId.Value);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>Records the delivery (ids only) until P2-F4 sends a receipt.</summary>
internal sealed partial class LogOnlinePaymentSucceeded(ILogger<LogOnlinePaymentSucceeded> logger) : IOutboxMessageHandler<OnlinePaymentSucceededMessage>
{
    public Task Handle(OnlinePaymentSucceededMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        LogDelivered(logger, message.OnlinePaymentId, message.PaymentId, context.TenantId, context.MessageId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Online payment {OnlinePaymentId} of school {TenantId} recorded payment {PaymentId} (outbox message {MessageId})")]
    private static partial void LogDelivered(ILogger logger, Guid onlinePaymentId, Guid paymentId, Guid? tenantId, Guid messageId);
}
