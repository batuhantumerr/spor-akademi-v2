using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Notifications;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;

namespace SporAkademi.Application.Billing;

/// <summary>
/// P3-13 (DEC-423): tells the payer's family that the money of an online payment was returned. Recipients are the fee
/// reminder recipients (guardians with an e-mail, or the adult student; DEC-356), read when the message runs. The
/// school's reason is never sent. Delivery is at least once: a failure for one recipient retries the message.
/// </summary>
internal sealed partial class SendPaymentRefundedEmail(
    IPaymentRepository payments,
    IStudentReminderContacts contacts,
    IGroupDirectory groups,
    ITenancyModule tenancy,
    IEmailSender emailSender,
    ICurrentUser currentUser,
    ILogger<SendPaymentRefundedEmail> logger) : IOutboxMessageHandler<PaymentRefundedMessage>
{
    public async Task Handle(PaymentRefundedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        var school = currentUser.RequireTenant();
        var payment = await payments.FindAsync(PaymentId.From(message.PaymentId), cancellationToken);
        var today = await tenancy.TodayAsync(school, cancellationToken);
        var family = (await contacts.GetAsync([message.StudentId], today, cancellationToken)).GetValueOrDefault(message.StudentId);
        if (payment is not { Status: PaymentStatus.Refunded } || family is null || family.Recipients.Count == 0)
        {
            LogSkipped(logger, message.RefundId, context.MessageId);
            return;
        }

        var group = (await groups.GetManyAsync([payment.GroupId.Value], cancellationToken)).GetValueOrDefault(payment.GroupId.Value);
        var schoolName = await tenancy.SchoolNameAsync(school, cancellationToken) ?? "Spor Akademi";
        foreach (var recipient in family.Recipients)
        {
            await emailSender.SendAsync(PaymentRefundEmails.Compose(payment, recipient, family, group?.Name, schoolName), cancellationToken);
        }

        LogSent(logger, message.RefundId, family.Recipients.Count, context.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} e-mailed to {Count} recipients (outbox message {MessageId})")]
    private static partial void LogSent(ILogger logger, Guid refundId, int count, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund {RefundId} has no e-mail recipient (outbox message {MessageId})")]
    private static partial void LogSkipped(ILogger logger, Guid refundId, Guid messageId);
}

/// <summary>The Turkish refund e-mail (DEC-350, DEC-423): who, which group and month, how much, and that the bank may take a few days.</summary>
internal static class PaymentRefundEmails
{
    public const string Template = "billing.payment-refunded";

    private static readonly string[] MonthNames =
        ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    public static EmailMessage Compose(Payment payment, ReminderRecipient recipient, ReminderContacts student, string? groupName, string schoolName)
    {
        var period = $"{MonthNames[payment.Period.Month - 1]} {payment.Period.Year}";
        var group = groupName is null ? string.Empty : $" ({groupName} grubu)";
        var body = new EmailBody($"Merhaba {recipient.FirstName} {recipient.LastName},")
            .Paragraph($"{student.FirstName} {student.LastName} için {schoolName}{group} {period} aidatına online olarak yapılan {EmailBody.Lira(payment.Amount.Amount)} tutarındaki ödeme, ödemenin yapıldığı karta iade edildi.")
            .Paragraph("İadenin hesabınıza yansıması bankanıza göre birkaç iş günü sürebilir.")
            .Paragraph("Sorularınız için okulunuzla iletişime geçin.");
        return body.Build(Template, recipient.Email, $"{period} aidatı ödemeniz iade edildi");
    }
}
