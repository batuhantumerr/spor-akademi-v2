namespace SporAkademi.Infrastructure.Notifications;

/// <summary>Configuration section <c>Email</c> (P2-F4, DEC-350). Without <c>Email:Smtp:Host</c> nothing is sent (log only).</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    public string FromAddress { get; set; } = "no-reply@spor-akademi.local";

    public string FromName { get; set; } = "Spor Akademi";

    /// <summary>The admin app's public address; e-mail links point here (e.g. <c>https://app.example.com/</c>).</summary>
    public string AppBaseUrl { get; set; } = "http://localhost:4200/";

    /// <summary>
    /// The parent app's public address (P3-02, DEC-383); a parent's links point here. Empty until the parent app is
    /// deployed: the links then use <see cref="AppBaseUrl"/>, whose set-password page works for every account.
    /// </summary>
    public string? ParentAppBaseUrl { get; set; }

    public SmtpOptions Smtp { get; set; } = new();

    internal bool IsValid() =>
        IsBaseAddress(AppBaseUrl)
        && (string.IsNullOrWhiteSpace(ParentAppBaseUrl) || IsBaseAddress(ParentAppBaseUrl))
        && Domain.SharedKernel.Email.Create(FromAddress).IsSuccess
        && !string.IsNullOrWhiteSpace(FromName)
        && Smtp.Port is > 0 and <= 65535
        && Smtp.Timeout > TimeSpan.Zero
        && Enum.IsDefined(Smtp.Security)
        && (string.IsNullOrEmpty(Smtp.UserName) || !string.IsNullOrEmpty(Smtp.Password));

    /// <summary>
    /// The environment rules (DEC-350): when Production sends e-mail, its links must point to a public https address
    /// (not the localhost default); and credentials are never sent over plain SMTP outside Development.
    /// </summary>
    internal bool IsValidIn(bool isDevelopment, bool isProduction) =>
        IsValid()
        && (!isProduction || string.IsNullOrWhiteSpace(Smtp.Host)
            || (IsPublicHttps(AppBaseUrl) && (string.IsNullOrWhiteSpace(ParentAppBaseUrl) || IsPublicHttps(ParentAppBaseUrl))))
        && (isDevelopment || Smtp.Security != SmtpSecurity.None || string.IsNullOrEmpty(Smtp.UserName));

    private static bool IsBaseAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsPublicHttps(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !uri.IsLoopback
        && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
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
