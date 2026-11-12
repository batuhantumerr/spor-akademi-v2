using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace SporAkademi.Api.Observability;

/// <summary>
/// P3-08 (DEC-399): the hub's WebSocket carries the access token in the query (<c>access_token=…</c>) and the hub's URLs
/// carry SignalR's connection token (<c>?id=…</c>, which is enough to read a long-polling connection's messages).
/// ASP.NET Core's request logs are off in code, but a configuration can turn a sub-category back on; this enricher
/// removes both values, plain or URL-encoded (<c>access_token%3D…</c>), from every property of every event (and so from
/// every message rendered from properties), whichever logger wrote it. Limits: an enricher cannot change an exception's
/// own text or a message template's literal text; the framework's request logs put URLs only in properties.
/// </summary>
internal sealed partial class AccessTokenRedaction : ILogEventEnricher
{
    public const string Redacted = "[REDACTED]";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (Redact(value) is { } redacted)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    public static string RedactText(string text) => SecretInQuery().Replace(text, "${key}${eq}" + Redacted);

    /// <returns>The redacted value, or <c>null</c> when nothing had to change.</returns>
    private static LogEventPropertyValue? Redact(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } when SecretInQuery().IsMatch(text) => new ScalarValue(RedactText(text)),
        ScalarValue { Value: { } other } when other is not string && other.ToString() is { } rendered && SecretInQuery().IsMatch(rendered)
            => new ScalarValue(RedactText(rendered)),
        SequenceValue sequence when sequence.Elements.Any(e => Redact(e) is not null)
            => new SequenceValue(sequence.Elements.Select(e => Redact(e) ?? e)),
        StructureValue structure when structure.Properties.Any(p => Redact(p.Value) is not null)
            => new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Value) ?? p.Value)), structure.TypeTag),
        DictionaryValue dictionary when dictionary.Elements.Any(e => Redact(e.Value) is not null)
            => new DictionaryValue(dictionary.Elements.Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Redact(e.Value) ?? e.Value))),
        _ => null,
    };

    /// <summary>
    /// <c>access_token</c> anywhere; <c>id</c> only as a query parameter (after <c>?</c> or <c>&amp;</c>, also encoded);
    /// <c>=</c> plain or encoded; the value up to the next parameter.
    /// </summary>
    [GeneratedRegex("""(?<key>access_token|(?<=[?&]|%3[Ff]|%26)id)(?<eq>=|%3[Dd])(?:(?!%26)[^&\s"'])*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretInQuery();
}
