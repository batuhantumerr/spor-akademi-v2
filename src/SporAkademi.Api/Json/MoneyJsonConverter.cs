using System.Text.Json;
using System.Text.Json.Serialization;
using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Json;

/// <summary>
/// Writes and reads <see cref="Money"/> in the API shape <c>{ "amount": "1250.00", "currency": "TRY" }</c>
/// (appendix D §1.2, DEC-066). Contracts use <see cref="MoneyDto"/>; this keeps a <see cref="Money"/> that
/// reaches the serializer from ever becoming a JSON number.
/// </summary>
internal sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be an object with a text amount and a currency.");
        }

        string? amount = null;
        string? currency = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Unexpected token in money.");
            }

            var name = reader.GetString();
            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Money fields are text.");
            }

            if (string.Equals(name, "amount", StringComparison.OrdinalIgnoreCase))
            {
                amount = reader.GetString();
            }
            else if (string.Equals(name, "currency", StringComparison.OrdinalIgnoreCase))
            {
                currency = reader.GetString();
            }
        }

        var money = new MoneyDto(amount!, currency!).ToMoney();
        return money.IsSuccess ? money.Value : throw new JsonException("Money is not valid.");
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteString("amount", value.ToAmountString());
        writer.WriteString("currency", value.Currency);
        writer.WriteEndObject();
    }
}
