using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Notifications;
using SporAkademi.Infrastructure.Notifications;

namespace SporAkademi.IntegrationTests.Notifications;

/// <summary>
/// P2-F4 (DEC-350, DEC-357): the MailKit sender against a real SMTP server, the same Mailpit image as the optional
/// compose service. The message arrives with the Turkish text intact, as plain text plus HTML, without attachments.
/// </summary>
[Trait("Story", "P2-F4")]
public sealed class SmtpEmailSenderTests : IAsyncLifetime
{
    private readonly MailpitContainer _mailpit = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _mailpit.StartAsync(Ct);

    public ValueTask DisposeAsync() => _mailpit.DisposeAsync();

    [Fact]
    public async Task A_message_arrives_as_plain_text_and_html_with_turkish_text_intact()
    {
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions
        {
            FromAddress = "no-reply@spor-akademi.local",
            Smtp = new SmtpOptions { Host = _mailpit.Hostname, Port = _mailpit.SmtpPort, Security = SmtpSecurity.None },
        }));
        var message = new EmailBody("Merhaba Deniz Kaya,")
            .Paragraph("Türkçe karakterler: ğüşıöç ĞÜŞİÖÇ")
            .Action("Şifremi belirle", new Uri("http://localhost:4200/reset-password#token=abc_DEF-123"))
            .Build("test.template", "deniz@example.com", "Spor Akademi şifre sıfırlama");

        await sender.SendAsync(message, Ct);

        using var http = new HttpClient { BaseAddress = _mailpit.ApiAddress };
        var list = await http.GetFromJsonAsync<JsonElement>("api/v1/messages", Ct);
        var summary = list.GetProperty("messages").EnumerateArray().ShouldHaveSingleItem();
        summary.GetProperty("Subject").GetString().ShouldBe("Spor Akademi şifre sıfırlama");
        summary.GetProperty("To")[0].GetProperty("Address").GetString().ShouldBe("deniz@example.com");
        summary.GetProperty("From").GetProperty("Address").GetString().ShouldBe("no-reply@spor-akademi.local");
        summary.GetProperty("From").GetProperty("Name").GetString().ShouldBe("Spor Akademi");

        var full = await http.GetFromJsonAsync<JsonElement>($"api/v1/message/{summary.GetProperty("ID").GetString()}", Ct);
        full.GetProperty("Text").GetString()!.ShouldContain("ğüşıöç ĞÜŞİÖÇ");
        full.GetProperty("Text").GetString()!.ShouldContain("http://localhost:4200/reset-password#token=abc_DEF-123");
        full.GetProperty("HTML").GetString()!.ShouldContain("href=\"http://localhost:4200/reset-password#token=abc_DEF-123\"");
        full.GetProperty("Attachments").GetArrayLength().ShouldBe(0);
        full.GetProperty("Inline").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task An_unreachable_server_throws_so_the_outbox_retries()
    {
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions
        {
            Smtp = new SmtpOptions { Host = "127.0.0.1", Port = 1, Security = SmtpSecurity.None, Timeout = TimeSpan.FromSeconds(5) },
        }));

        await Should.ThrowAsync<Exception>(() => sender.SendAsync(new EmailMessage("t", "deniz@example.com", "s", "x", "<p>x</p>"), Ct));
    }
}

/// <summary>A Mailpit server like the compose one (DEC-357): image (tag + digest) read from compose.yaml.</summary>
public sealed partial class MailpitContainer : IAsyncDisposable
{
    private const int Smtp = 1025;
    private const int Http = 8025;

    private readonly IContainer _container = new ContainerBuilder(ReadMailpitImage())
        .WithEnvironment("MP_DISABLE_VERSION_CHECK", "true")
        .WithPortBinding(Smtp, assignRandomHostPort: true)
        .WithPortBinding(Http, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(Http).ForPath("/readyz")))
        .Build();

    public string Hostname => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(Smtp);

    public Uri ApiAddress => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Http)}/");

    public Task StartAsync(CancellationToken cancellationToken = default) => _container.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string ReadMailpitImage()
    {
        var compose = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "compose.yaml"));
        var match = MailpitImage().Match(compose);
        return match.Success
            ? match.Groups["image"].Value
            : throw new InvalidOperationException("compose.yaml has no mailpit image.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>axllent/mailpit:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex MailpitImage();
}
