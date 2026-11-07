using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SporAkademi.Application.Abstractions.Notifications;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>The test API's e-mail sender (DEC-350): keeps every message instead of sending it, or fails on demand.</summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    /// <summary>When set, sends throw it (an SMTP outage), for every message or those <see cref="FailWhen"/> selects.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Limits <see cref="FailWith"/> to some messages (e.g. one recipient).</summary>
    public Func<EmailMessage, bool>? FailWhen { get; set; }

    public IReadOnlyList<EmailMessage> Sent => [.. _sent];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (FailWith is { } failure && (FailWhen is null || FailWhen(message)))
        {
            throw failure;
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string address) =>
        [.. _sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase))];

    public void Clear() => _sent.Clear();

    /// <summary>The set-password token of a link in <paramref name="message"/> (from the URL fragment).</summary>
    public static string TokenIn(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var match = TokenPattern().Match(message.TextBody);
        match.Success.ShouldBeTrue("The e-mail has no set-password link.");
        return Uri.UnescapeDataString(match.Groups["token"].Value);
    }

    [GeneratedRegex(@"/reset-password#token=(?<token>[A-Za-z0-9_%\-]+)")]
    private static partial Regex TokenPattern();
}
