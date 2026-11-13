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
                // A welcome link is pointless once the user chose a password (e.g. with the temporary one); so is an
                // invitation the parent already accepted (DEC-383).
                if (user is null
                    || (purpose is PasswordTokenPurpose.Welcome or PasswordTokenPurpose.Invitation && !user.MustChangePassword)
                    || (purpose == PasswordTokenPurpose.Invitation && user.Role != UserRole.Parent))
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
        // DEC-383: a parent's links open the parent app; staff links the admin panel.
        var setPassword = recipient.Role == UserRole.Parent ? links.ParentSetPassword(token.Token) : links.SetPassword(token.Token);
        var message = purpose switch
        {
            PasswordTokenPurpose.Welcome => IdentityEmails.Welcome(recipient, school, setPassword, links.ForgotPassword()),
            PasswordTokenPurpose.Invitation => IdentityEmails.ParentInvitation(recipient, school, setPassword),
            _ => IdentityEmails.PasswordReset(recipient, setPassword),
        };
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

/// <summary>P3-02 (DEC-383): the parent invitation e-mail.</summary>
internal sealed class SendParentInvitationEmail(PasswordLinkMailer mailer) : IOutboxMessageHandler<ParentInvitedMessage>
{
    public Task Handle(ParentInvitedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return mailer.SendAsync(message.UserId, PasswordTokenPurpose.Invitation, context, cancellationToken);
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

/// <summary>P4-03 (DEC-512): tells the owner that the password of their account changed. No link, token or password.</summary>
internal sealed partial class SendPasswordChangedEmail(
    IUserRepository users,
    IEmailSender emailSender,
    ILogger<SendPasswordChangedEmail> logger) : IOutboxMessageHandler<PasswordChangedMessage>
{
    public async Task Handle(PasswordChangedMessage message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var user = await users.GetByIdAsync(UserId.From(message.UserId), cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            return;
        }

        var email = IdentityEmails.PasswordChanged(user);
        await emailSender.SendAsync(email, cancellationToken);
        LogSent(logger, email.Template, message.UserId, context.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail {Template} sent to user {UserId} (outbox message {MessageId})")]
    private static partial void LogSent(ILogger logger, string template, Guid userId, Guid messageId);
}

/// <summary>The Turkish identity e-mails (DEC-350). The welcome e-mail never contains the temporary password (DEC-351).</summary>
internal static class IdentityEmails
{
    public const string WelcomeTemplate = "identity.welcome";
    public const string PasswordResetTemplate = "identity.password-reset";
    public const string PasswordChangedTemplate = "identity.password-changed";
    public const string ParentInvitationTemplate = "identity.parent-invitation";

    /// <summary>P3-02 (DEC-383): no child's name and no password; only the school, the login and the link.</summary>
    public static EmailMessage ParentInvitation(User user, string? schoolName, Uri setPassword)
    {
        var who = schoolName is null ? "Okulunuz" : schoolName;
        return new EmailBody($"Merhaba {user.FirstName} {user.LastName},")
            .Paragraph($"{who} sizi Spor Akademi veli hesabına davet etti. Veli hesabıyla çocuğunuzun antrenman programını, yoklamalarını, aidat borçlarını ve duyurularını görebilir, aidatı online ödeyebilirsiniz.")
            .Paragraph($"Giriş için kullanacağınız e-posta adresi: {user.Email.Value}")
            .Paragraph($"Daveti kabul etmek ve şifrenizi belirlemek için aşağıdaki bağlantıyı kullanın. Bağlantı {Lifetime(PasswordTokenPurpose.Invitation)} geçerlidir ve yalnızca bir kez kullanılabilir.")
            .Action("Daveti kabul et", setPassword)
            .Paragraph("Bağlantının süresi dolarsa okulunuzdan daveti yeniden göndermesini isteyin.")
            .Paragraph("Bu daveti beklemiyorsanız bu e-postayı dikkate almayın; hesap siz şifre belirlemeden kullanılamaz.")
            .Build(ParentInvitationTemplate, user.Email.Value, "Spor Akademi veli hesabı daveti");
    }

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

    /// <summary>P4-03 (DEC-512): says only that it happened, never the password and never a link an attacker could mimic.</summary>
    public static EmailMessage PasswordChanged(User user) =>
        new EmailBody($"Merhaba {user.FirstName} {user.LastName},")
            .Paragraph("Spor Akademi hesabınızın şifresi değiştirildi. Bütün cihazlardaki oturumlarınız kapandı.")
            .Paragraph("Bu değişikliği siz yaptıysanız bir şey yapmanız gerekmez.")
            .Paragraph("Siz yapmadıysanız hesabınız başkasının eline geçmiş olabilir: okul yöneticinizle hemen iletişime geçin ve giriş sayfasındaki \"Şifremi unuttum\" ile şifrenizi yeniden belirleyin.")
            .Build(PasswordChangedTemplate, user.Email.Value, "Spor Akademi şifreniz değiştirildi");

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
        UserRole.Parent => "veli",
        _ => "platform yöneticisi",
    };

    private static string Lifetime(PasswordTokenPurpose purpose)
    {
        var lifetime = PasswordToken.LifetimeOf(purpose);
        return lifetime.TotalHours >= 1 ? $"{lifetime.TotalHours:0} saat" : $"{lifetime.TotalMinutes:0} dakika";
    }
}
