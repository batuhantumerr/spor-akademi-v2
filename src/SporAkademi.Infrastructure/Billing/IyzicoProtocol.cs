using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SporAkademi.Infrastructure.Billing;

/// <summary>
/// iyzico's documented signing (DEC-345; docs.iyzico.com, "HMACSHA256 Auth" and "Webhook"). The Fake provider signs
/// its webhooks the same way with its own key, so the verification path is exercised by every test.
/// </summary>
internal static class IyzicoProtocol
{
    public const string SignatureHeader = "X-IYZ-SIGNATURE-V3";
    public const string RandomHeader = "x-iyzi-rnd";

    /// <summary>
    /// <c>Authorization: IYZWSv2 base64("apiKey:" + apiKey + "&amp;randomKey:" + rnd + "&amp;signature:" + hex(HMACSHA256(secretKey, rnd + uriPath + body)))</c>.
    /// </summary>
    public static string Authorization(string apiKey, string secretKey, string randomKey, string uriPath, string body)
    {
        var signature = HexHmac(secretKey, randomKey + uriPath + body);
        var raw = $"apiKey:{apiKey}&randomKey:{randomKey}&signature:{signature}";
        return "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// The URI path that IYZWSv2 signs: the request path up to (not including) its query string. The official SDKs
    /// (iyzipay-dotnet `IyzipayResourceV2.FindUriPath`, MIT) cut the URL at `?`, so query parameters are never signed.
    /// </summary>
    public static string SignedPath(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? pathAndQuery : pathAndQuery[..query];
    }

    /// <summary>
    /// The hosted-checkout (Checkout Form) webhook signature:
    /// <c>hex(HMACSHA256(secretKey, secretKey + iyziEventType + iyziPaymentId + token + paymentConversationId + status))</c>.
    /// </summary>
    public static string WebhookSignature(string secretKey, HostedWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return HexHmac(secretKey, secretKey + webhook.EventType + webhook.PaymentId + webhook.Token + webhook.ConversationId + webhook.Status);
    }

    /// <summary>Reads and verifies a hosted-checkout webhook; <c>null</c> when it is malformed or the signature does not match.</summary>
    public static HostedWebhook? VerifyWebhook(string secretKey, string body, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var signature = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        HostedWebhook? webhook;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            webhook = root.ValueKind == JsonValueKind.Object
                ? new HostedWebhook(
                    Text(root, "iyziEventType"),
                    Text(root, "iyziPaymentId"),
                    Text(root, "token"),
                    Text(root, "paymentConversationId"),
                    Text(root, "status"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (webhook is null || webhook.Token.Length == 0 || webhook.EventType.Length == 0 || webhook.Status.Length == 0)
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(WebhookSignature(secretKey, webhook));
        var actual = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? webhook : null;
    }

    public static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    private static string HexHmac(string key, string data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)));
}

/// <summary>The fields of a hosted-checkout webhook that are signed; nothing else is read (no personal data).</summary>
internal sealed record HostedWebhook(string EventType, string PaymentId, string Token, string ConversationId, string Status)
{
    /// <summary>De-duplication key (DEC-343): the same event, payment and status is the same delivery.</summary>
    public string EventKey
    {
        get
        {
            var key = $"{EventType}:{PaymentId}:{Status}:{Token}";
            return key.Length <= Domain.Billing.PaymentNotification.EventKeyMaxLength
                ? key
                : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["iyziEventType"] = EventType,
        ["iyziPaymentId"] = PaymentId,
        ["token"] = Token,
        ["paymentConversationId"] = ConversationId,
        ["status"] = Status,
    });
}
