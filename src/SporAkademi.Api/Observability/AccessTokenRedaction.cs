using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace SporAkademi.Api.Observability;

/// <summary>
/// P3-08 (DEC-399): the hub's WebSocket carries the access token in the query (<c>access_token=…</c>). ASP.NET Core's
/// request logs are off in code, but a configuration can turn a sub-category back on; this enricher removes the value
/// from every string property (and so from every rendered message) of every event, whichever logger wrote it.
/// </summary>
internal sealed partial class AccessTokenRedaction : ILogEventEnricher
{
    public const string Redacted = "access_token=[REDACTED]";

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

    public static string RedactText(string text) => TokenInQuery().Replace(text, Redacted);

    /// <returns>The redacted value, or <c>null</c> when nothing had to change.</returns>
    private static LogEventPropertyValue? Redact(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } when TokenInQuery().IsMatch(text) => new ScalarValue(RedactText(text)),
        ScalarValue { Value: { } other } when other is not string && other.ToString() is { } rendered && TokenInQuery().IsMatch(rendered)
            => new ScalarValue(RedactText(rendered)),
        SequenceValue sequence when sequence.Elements.Any(e => Redact(e) is not null)
            => new SequenceValue(sequence.Elements.Select(e => Redact(e) ?? e)),
        StructureValue structure when structure.Properties.Any(p => Redact(p.Value) is not null)
            => new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Value) ?? p.Value)), structure.TypeTag),
        DictionaryValue dictionary when dictionary.Elements.Any(e => Redact(e.Value) is not null)
            => new DictionaryValue(dictionary.Elements.Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Redact(e.Value) ?? e.Value))),
        _ => null,
    };

    [GeneratedRegex("access_token=[^&\\s\"']*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenInQuery();
}
