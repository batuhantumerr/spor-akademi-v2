namespace SporAkademi.Infrastructure.Notifications;

/// <summary>Configuration section <c>Email</c> (P2-F4, DEC-350). Without <c>Email:Smtp:Host</c> nothing is sent (log only).</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public string FromAddress { get; set; } = "no-reply@spor-akademi.local";

    public string FromName { get; set; } = "Spor Akademi";

    /// <summary>The admin app's public address; e-mail links point here (e.g. <c>https://app.example.com/</c>).</summary>
    public string AppBaseUrl { get; set; } = "http://localhost:4200/";

    public SmtpOptions Smtp { get; set; } = new();

    internal bool IsValid() =>
        Uri.TryCreate(AppBaseUrl, UriKind.Absolute, out var app)
        && (app.Scheme == Uri.UriSchemeHttps || app.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(app.Query) && string.IsNullOrEmpty(app.Fragment)
        && Domain.SharedKernel.Email.Create(FromAddress).IsSuccess
        && !string.IsNullOrWhiteSpace(FromName)
        && Smtp.Port is > 0 and <= 65535
        && Smtp.Timeout > TimeSpan.Zero
        && Enum.IsDefined(Smtp.Security)
        && (string.IsNullOrEmpty(Smtp.UserName) || !string.IsNullOrEmpty(Smtp.Password));
}

public sealed class SmtpOptions
{
    /// <summary>Empty: e-mail is not sent, only its template name is logged (DEC-350).</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>TLS by default; <see cref="SmtpSecurity.None"/> only for a local catcher such as Mailpit.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}

public enum SmtpSecurity
{
    /// <summary>Plain SMTP, for a local development catcher only.</summary>
    None,

    /// <summary>Upgrade with STARTTLS; fails if the server does not offer it.</summary>
    StartTls,

    /// <summary>Implicit TLS (usually port 465).</summary>
    SslOnConnect,
}
