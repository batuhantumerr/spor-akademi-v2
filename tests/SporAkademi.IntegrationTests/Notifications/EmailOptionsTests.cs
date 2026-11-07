using SporAkademi.Infrastructure.Notifications;

namespace SporAkademi.IntegrationTests.Notifications;

/// <summary>DEC-350: environment rules for the e-mail settings, checked at start.</summary>
[Trait("Story", "P2-F4")]
public sealed class EmailOptionsTests
{
    [Theory]
    [InlineData("http://localhost:4200/", null, true)] // Production without SMTP sends nothing, so the default is fine
    [InlineData("http://localhost:4200/", "smtp.example.com", false)]
    [InlineData("http://app.example.com/", "smtp.example.com", false)]
    [InlineData("https://127.0.0.1/", "smtp.example.com", false)]
    [InlineData("https://admin.localhost/", "smtp.example.com", false)]
    [InlineData("https://app.example.com/", "smtp.example.com", true)]
    public void Production_links_must_be_public_https_when_e_mail_is_sent(string appBaseUrl, string? host, bool valid)
    {
        var options = new EmailOptions { AppBaseUrl = appBaseUrl, Smtp = new SmtpOptions { Host = host } };

        options.IsValidIn(isDevelopment: false, isProduction: true).ShouldBe(valid);
        options.IsValidIn(isDevelopment: true, isProduction: false).ShouldBeTrue();
    }

    [Fact]
    public void Credentials_never_go_over_plain_smtp_outside_development()
    {
        var options = new EmailOptions
        {
            Smtp = new SmtpOptions { Host = "127.0.0.1", Security = SmtpSecurity.None, UserName = "user", Password = "secret-test-value" },
        };

        options.IsValidIn(isDevelopment: true, isProduction: false).ShouldBeTrue();
        options.IsValidIn(isDevelopment: false, isProduction: false).ShouldBeFalse();
    }
}
