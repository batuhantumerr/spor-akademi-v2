using System.Text.Json;
using SporAkademi.Api.Json;
using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.IntegrationTests.Platform;

/// <summary>Appendix D §1.2, DEC-066: money is <c>{ "amount": "1250.00", "currency": "TRY" }</c> on the wire.</summary>
[Trait("Story", "US-23")]
public sealed class MoneyJsonTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new MoneyJsonConverter() } };

    [Theory]
    [InlineData(1250, "1250.00")]
    [InlineData(0.1, "0.10")]
    [InlineData(9999999999.99, "9999999999.99")]
    public void Money_round_trips_as_text_with_two_decimals(decimal amount, string text)
    {
        var json = JsonSerializer.Serialize(Money.Try(amount), Options);

        json.ShouldBe($$"""{"amount":"{{text}}","currency":"TRY"}""");
        JsonSerializer.Deserialize<Money>(json, Options).ShouldBe(Money.Try(amount));
    }

    [Theory]
    [InlineData("""{"amount":1250,"currency":"TRY"}""")]
    [InlineData("""{"amount":"12.345","currency":"TRY"}""")]
    [InlineData("""{"amount":"10.00","currency":"EUR"}""")]
    [InlineData("\"10.00\"")]
    public void Anything_but_text_money_is_rejected(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Money>(json, Options));
    }

    [Fact]
    public void The_dto_has_the_same_shape()
    {
        var json = JsonSerializer.Serialize(MoneyDto.From(Money.Try(1250.5m)), Options);

        json.ShouldBe("""{"amount":"1250.50","currency":"TRY"}""");
        JsonSerializer.Deserialize<MoneyDto>(json, Options)!.ToMoney().Value.ShouldBe(Money.Try(1250.5m));
    }
}
