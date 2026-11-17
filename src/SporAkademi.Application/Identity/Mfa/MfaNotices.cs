using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Notifications;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Identity.Passwords;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Mfa;

/// <summary>P5-01 (DEC-607): a change of the second factor becomes the notice to the account's owner, in the same transaction.</summary>
internal sealed class PublishMfaNotice(IOutbox outbox, IUserRepository users) : IDomainEventConsumer<MfaNoticeRaised>
{
    public async Task<Result> Handle(MfaNoticeRaised domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var user = await users.GetByIdAsync(domainEvent.UserId, cancellationToken);
        if (user is not null)
        {
            outbox.Enqueue(new MfaNoticeMessage(user.Id.Value, domainEvent.Notice.ToString()), user.TenantId?.Value);
        }

        return Result.Success();
    }
}

/// <summary>P5-01 (DEC-607): tells the owner that MFA of their account changed. Says what happened, never a code or a link.</summary>
[OutboxConsumerGroup(OutboxConsumerGroups.Notifications)]
internal sealed partial class SendMfaNoticeEmail(
    IUserRepository users,
    IEmailSender emailSender,
    ILogger<SendMfaNoticeEmail> logger) : IOutboxMessageHandler<MfaNoticeMessage>
{
    public async Task Handle(MfaNoticeMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var user = await users.GetByIdAsync(UserId.From(message.UserId), cancellationToken);
        if (user is null || user.Status != UserStatus.Active || !Enum.TryParse<MfaNotice>(message.Notice, out var notice))
        {
            return;
        }

        var email = IdentityEmails.MfaChanged(user, notice);
        await emailSender.SendAsync(email, cancellationToken);
        LogSent(logger, email.Template, message.UserId, context.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail {Template} sent to user {UserId} (outbox message {MessageId})")]
    private static partial void LogSent(ILogger logger, string template, Guid userId, Guid messageId);
}
