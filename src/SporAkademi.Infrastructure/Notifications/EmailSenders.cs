using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SporAkademi.Application.Abstractions.Notifications;

namespace SporAkademi.Infrastructure.Notifications;

/// <summary>
/// Sends through SMTP with MailKit (MIT, DEC-350): one connection per message, TLS unless configured otherwise. The
/// message is plain text with an HTML alternative. Nothing about the message is logged here; a failure throws and the
/// outbox stores and logs only the exception type (DEC-302).
/// </summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;
        var smtp = settings.Smtp;

        using var mime = Build(message, settings);
        using var client = new SmtpClient { Timeout = (int)smtp.Timeout.TotalMilliseconds };
        await client.ConnectAsync(smtp.Host ?? throw new InvalidOperationException("Email:Smtp:Host is not configured."), smtp.Port, Socket(smtp.Security), cancellationToken);
        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    internal static MimeMessage Build(EmailMessage message, EmailOptions settings)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    private static SecureSocketOptions Socket(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTls,
    };
}

/// <summary>
/// Used when SMTP is not configured (DEC-350): nothing is sent, and only the template name is logged, never the
/// address, names or links.
/// </summary>
internal sealed partial class LogOnlyEmailSender(ILogger<LogOnlyEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogNotSent(logger, message.Template);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail {Template} not sent: SMTP is not configured (Email:Smtp:Host)")]
    private static partial void LogNotSent(ILogger logger, string template);
}

/// <summary>Links of the admin app (DEC-351): the token travels in the fragment, which browsers never send to a server.</summary>
internal sealed class AppLinks(IOptions<EmailOptions> options) : IAppLinks
{
    public Uri SetPassword(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return new Uri(Base, "reset-password#token=" + Uri.EscapeDataString(token));
    }

    public Uri ForgotPassword() => new(Base, "forgot-password");

    private Uri Base
    {
        get
        {
            var text = options.Value.AppBaseUrl;
            return new Uri(text.EndsWith('/') ? text : text + "/", UriKind.Absolute);
        }
    }
}

internal static class EmailRegistration
{
    public static IServiceCollection AddEmail(this IServiceCollection services)
    {
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.Section)
            .Validate<IHostEnvironment>(
                (o, environment) => o.IsValidIn(environment.IsDevelopment(), environment.IsProduction()),
                "The Email configuration section has an invalid value (in Production an https, non-localhost AppBaseUrl when SMTP is set; no SMTP credentials without TLS outside Development).")
            .ValidateOnStart();

        services.AddSingleton<IAppLinks, AppLinks>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddSingleton<LogOnlyEmailSender>();
        // Chosen when first resolved, so test hosts and scripts can supply the setting through any configuration source.
        services.AddSingleton<IEmailSender>(provider =>
            string.IsNullOrWhiteSpace(provider.GetRequiredService<IOptions<EmailOptions>>().Value.Smtp.Host)
                ? provider.GetRequiredService<LogOnlyEmailSender>()
                : provider.GetRequiredService<SmtpEmailSender>());
        return services;
    }
}
