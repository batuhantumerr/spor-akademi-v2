using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace SporAkademi.Application.Abstractions.Notifications;

/// <summary>
/// One e-mail to one recipient (P2-F4). <see cref="Template"/> is a stable name such as <c>identity.welcome</c>: the
/// only thing about an e-mail that may be logged (NFR-PRV-03), never the address, the names or the link.
/// </summary>
public sealed record EmailMessage(string Template, string To, string Subject, string TextBody, string HtmlBody)
{
    /// <summary>Address, subject and bodies hold personal data and links with tokens; keep them out of any log or exception text.</summary>
    public override string ToString() => $"EmailMessage {{ Template = {Template} }}";
}

/// <summary>
/// Sends e-mail (DEC-350). Called only from outbox handlers, after the business change committed (DEC-300), never
/// inside a request. Implementations: SMTP with MailKit; a log-only sender when SMTP is not configured; a capturing
/// fake in tests. A failure throws; the outbox retries the message.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Links into the admin app that e-mails carry (configured base address, DEC-351).</summary>
public interface IAppLinks
{
    /// <summary>
    /// The "Yeni şifre belirle" page with the token in the URL fragment: browsers do not send fragments to servers,
    /// so the token never reaches web server or proxy logs.
    /// </summary>
    Uri SetPassword(string token);

    /// <summary>The "Şifremi unuttum" page.</summary>
    Uri ForgotPassword();
}

/// <summary>
/// Builds the plain-text and simple HTML bodies of the Turkish e-mails (DEC-350): inline styles only, no images, no
/// external resources and no tracking pixels or tracked links. Every value is HTML-encoded.
/// </summary>
public sealed class EmailBody
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _html = new();

    public EmailBody(string greeting)
    {
        _text.Append(greeting).Append("\n\n");
        _html.Append("<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\"></head>")
            .Append("<body style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.5;color:#0f172a;\">")
            .Append("<p>").Append(Encode(greeting)).Append("</p>");
    }

    public EmailBody Paragraph(string text)
    {
        _text.Append(text).Append("\n\n");
        _html.Append("<p>").Append(Encode(text)).Append("</p>");
        return this;
    }

    /// <summary>A call to action: a plain link in HTML, the address on its own line in text.</summary>
    public EmailBody Action(string label, Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var address = link.AbsoluteUri;
        _text.Append(label).Append(":\n").Append(address).Append("\n\n");
        _html.Append("<p><a href=\"").Append(Encode(address))
            .Append("\" style=\"display:inline-block;padding:10px 16px;background:#2563eb;color:#ffffff;text-decoration:none;border-radius:6px;\">")
            .Append(Encode(label)).Append("</a></p>")
            .Append("<p style=\"font-size:13px;color:#475569;\">")
            .Append(Encode("Bağlantı çalışmazsa bu adresi tarayıcınıza yapıştırın:"))
            .Append("<br>").Append(Encode(address)).Append("</p>");
        return this;
    }

    public EmailMessage Build(string template, string to, string subject)
    {
        const string Footer = "Bu e-posta Spor Akademi tarafından otomatik gönderildi; lütfen yanıtlamayın.";
        var text = _text.ToString() + "--\n" + Footer + "\n";
        var html = _html.ToString()
            + "<hr style=\"border:none;border-top:1px solid #e2e8f0;\"><p style=\"font-size:12px;color:#64748b;\">"
            + Encode(Footer) + "</p></body></html>";
        return new EmailMessage(template, to, subject, text, html);
    }

    /// <summary><c>gg.AA.yyyy</c> (NFR-CMP).</summary>
    public static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary><c>₺1.250,00</c> (NFR-CMP).</summary>
    public static string Lira(decimal amount) => "₺" + amount.ToString("#,##0.00", TurkishCulture);

    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Encodes markup characters but keeps Turkish letters readable (no numeric entities).</summary>
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    private static string Encode(string value) => Html.Encode(value);
}
