using System.Diagnostics;
using OpenTelemetry;
using SporAkademi.Application.Abstractions.Observability;

namespace SporAkademi.Api.Observability;

/// <summary>
/// P4-07 (DEC-551): runs on every finished span before any exporter. Spans carry routes, status codes, ids and
/// parameterised SQL; this removes whatever could still carry personal data or a secret:
/// <list type="bullet">
/// <item>query strings (<c>url.query</c>, and the query part of <c>url.full</c>): the hub's <c>access_token</c> and
/// connection id, a payment return token, search terms;</item>
/// <item>request and response headers, client address and port, user agent, any recorded SQL parameter;</item>
/// <item>a framework or driver status description (it may be an exception message); the application's own codes stay.</item>
/// </list>
/// Any remaining string attribute still matching a hub secret in a query is masked as in the logs (DEC-399).
/// </summary>
internal sealed class TelemetryScrubber : BaseProcessor<Activity>
{
    private static readonly string[] DroppedKeys =
    [
        "url.query", "http.query", "client.address", "client.port", "network.peer.address", "network.peer.port",
        "user_agent.original", "http.user_agent", "http.client_ip", "net.peer.ip", "net.sock.peer.addr",
        "enduser.id", "enduser.role", "db.user",
    ];

    private static readonly string[] DroppedPrefixes = ["http.request.header.", "http.response.header.", "db.query.parameter.", "db.statement.parameter"];

    private static readonly string[] UrlKeys = ["url.full", "http.url", "http.target"];

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var (key, value) in data.TagObjects.ToList())
        {
            if (Dropped(key))
            {
                data.SetTag(key, null);
            }
            else if (value is string text)
            {
                var scrubbed = UrlKeys.Contains(key, StringComparer.Ordinal) ? WithoutQuery(text) : AccessTokenRedaction.RedactText(text);
                if (!string.Equals(scrubbed, text, StringComparison.Ordinal))
                {
                    data.SetTag(key, scrubbed);
                }
            }
        }

        if (data.StatusDescription is not null && data.Source.Name != AppTelemetry.Name)
        {
            data.SetStatus(data.Status);
        }
    }

    private static bool Dropped(string key) =>
        DroppedKeys.Contains(key, StringComparer.Ordinal) || DroppedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    private static string WithoutQuery(string url)
    {
        var end = url.IndexOfAny(['?', '#']);
        return end < 0 ? url : url[..end];
    }
}
