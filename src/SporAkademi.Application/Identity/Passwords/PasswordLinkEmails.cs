using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Notifications;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Passwords;

/// <summary>
/// Creates a set-password token and e-mails its link (P2-F4, DEC-351 – DEC-353). The token is created here, after the
/// request committed, in its own transaction: only its hash is stored, and the clear token exists only in the
/// e-mail. A retried delivery creates a new token, which replaces the previous one. Logs carry ids only.
/// </summary>
internal sealed partial class PasswordLinkMailer(
    IUnitOfWork unitOfWork,
    IUserRepository users,
    IPasswordTokenFactory passwordTokens,
    ITenancyModule tenancy,
    IAppLinks links,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<PasswordLinkMailer> logger)
{
    public async Task SendAsync(Guid userId, PasswordTokenPurpose purpose, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (purpose == PasswordTokenPurpose.Reset && now - context.OccurredAt > PasswordToken.LifetimeOf(purpose))
        {
            // A request delayed (e.g. by retries) past a link's lifetime is stale; the user asks again.
            LogSkipped(logger, purpose, userId, context.MessageId, "Stale");
            return;
        }

        NewPasswordToken? token = null;
        User? recipient = null;
        var issued = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var user = await users.GetByIdAsync(UserId.From(userId), ct);
                // A welcome link is pointless once the user chose a password (e.g. with the temporary one).
                if (user is null || (purpose == PasswordTokenPurpose.Welcome && !user.MustChangePassword))
                {
                    return Result.Success(false);
                }

                var created = passwordTokens.Create();
                if (user.IssuePasswordToken(created.Hash, purpose, now).IsFailure)
                {
                    return Result.Success(false);
                }

                (token, recipient) = (created, user);
                return Result.Success(true);
            },
            cancellationToken);

        if (!issued.IsSuccess || !issued.Value || token is null || recipient is null)
        {
            LogSkipped(logger, purpose, userId, context.MessageId, issued.IsSuccess ? "NotApplicable" : issued.Error.Code);
            return;
        }

        var school = recipient.TenantId is { } schoolId ? await tenancy.SchoolNameAsync(schoolId, cancellationToken) : null;
        var message = purpose == PasswordTokenPurpose.Welcome
            ? IdentityEmails.Welcome(recipient, school, links.SetPassword(token.Token), links.ForgotPassword())
            : IdentityEmails.PasswordReset(recipient, links.SetPassword(token.Token));
        await emailSender.SendAsync(message, cancellationToken);
        LogSent(logger, message.Template, userId, context.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail {Template} sent to user {UserId} (outbox message {MessageId})")]
    private static partial void LogSent(ILogger logger, string template, Guid userId, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Purpose} link for user {UserId} not sent: {Reason} (outbox message {MessageId})")]
    private static partial void LogSkipped(ILogger logger, PasswordTokenPurpose purpose, Guid userId, Guid messageId, string reason);
}

internal sealed class SendWelcomeEmail(PasswordLinkMailer mailer) : IOutboxMessageHandler<AccountOpenedMessage>
{
    public Task Handle(AccountOpenedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return mailer.SendAsync(message.UserId, PasswordTokenPurpose.Welcome, context, cancellationToken);
    }
}

internal sealed class SendPasswordResetEmail(PasswordLinkMailer mailer) : IOutboxMessageHandler<PasswordResetRequestedMessage>
{
    public Task Handle(PasswordResetRequestedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return mailer.SendAsync(message.UserId, PasswordTokenPurpose.Reset, context, cancellationToken);
    }
}

/// <summary>The Turkish identity e-mails (DEC-350). The welcome e-mail never contains the temporary password (DEC-351).</summary>
internal static class IdentityEmails
{
    public const string WelcomeTemplate = "identity.welcome";
    public const string PasswordResetTemplate = "identity.password-reset";

    public static EmailMessage Welcome(User user, string? schoolName, Uri setPassword, Uri forgotPassword)
    {
        var where = schoolName is null ? "Spor Akademi yönetim paneline" : $"{schoolName} için Spor Akademi yönetim paneline";
        return new EmailBody($"Merhaba {user.FirstName} {user.LastName},")
            .Paragraph($"{where} {RoleName(user.Role)} hesabınız açıldı. Giriş için kullanacağınız e-posta adresi: {user.Email.Value}")
            .Paragraph($"Kendi şifrenizi belirlemek için aşağıdaki bağlantıyı kullanın. Bağlantı {Lifetime(PasswordTokenPurpose.Welcome)} geçerlidir ve yalnızca bir kez kullanılabilir.")
            .Action("Şifremi belirle", setPassword)
            .Paragraph("Bağlantının süresi dolarsa giriş sayfasındaki \"Şifremi unuttum\" ile yeni bir bağlantı isteyebilirsiniz:")
            .Action("Şifremi unuttum", forgotPassword)
            .Paragraph("Bu e-postayı beklemiyorsanız okulunuzun yöneticisine haber verin.")
            .Build(WelcomeTemplate, user.Email.Value, "Spor Akademi hesabınız açıldı");
    }

    public static EmailMessage PasswordReset(User user, Uri setPassword) =>
        new EmailBody($"Merhaba {user.FirstName} {user.LastName},")
            .Paragraph($"Spor Akademi hesabınız için şifre sıfırlama isteği aldık. Yeni şifrenizi belirlemek için aşağıdaki bağlantıyı kullanın. Bağlantı {Lifetime(PasswordTokenPurpose.Reset)} geçerlidir ve yalnızca bir kez kullanılabilir.")
            .Action("Yeni şifre belirle", setPassword)
            .Paragraph("Yeni şifrenizi belirlediğinizde bütün cihazlardaki oturumlarınız kapanır.")
            .Paragraph("Bu isteği siz yapmadıysanız bu e-postayı dikkate almayın; şifreniz değişmez.")
            .Build(PasswordResetTemplate, user.Email.Value, "Spor Akademi şifre sıfırlama");

    private static string RoleName(UserRole role) => role switch
    {
        UserRole.SchoolAdmin => "okul yöneticisi",
        UserRole.Coach => "antrenör",
        _ => "platform yöneticisi",
    };

    private static string Lifetime(PasswordTokenPurpose purpose)
    {
        var lifetime = PasswordToken.LifetimeOf(purpose);
        return lifetime.TotalHours >= 1 ? $"{lifetime.TotalHours:0} saat" : $"{lifetime.TotalMinutes:0} dakika";
    }
}
