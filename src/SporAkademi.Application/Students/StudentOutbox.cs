using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students;

/// <summary>P2-F3: a committed leave becomes an integration message, written in the same transaction as the leave.</summary>
internal sealed class PublishStudentLeft(IOutbox outbox) : IDomainEventConsumer<StudentLeft>
{
    public Task<Result> Handle(StudentLeft domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(new StudentLeftMessage(domainEvent.StudentId.Value, domainEvent.LeftAt), domainEvent.TenantId.Value);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>
/// The first outbox handler: records the delivery (ids only). It proves the after-commit path end to end until
/// P2-F4 adds real notifications.
/// </summary>
internal sealed partial class LogStudentLeft(ILogger<LogStudentLeft> logger) : IOutboxMessageHandler<StudentLeftMessage>
{
    public Task Handle(StudentLeftMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        LogDelivered(logger, message.StudentId, context.TenantId, context.MessageId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Student {StudentId} of school {TenantId} left (outbox message {MessageId})")]
    private static partial void LogDelivered(ILogger logger, Guid studentId, Guid? tenantId, Guid messageId);
}
