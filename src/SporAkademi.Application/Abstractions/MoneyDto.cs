using System.Globalization;
using System.Text.RegularExpressions;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Abstractions;

/// <summary>
/// Money on the wire (appendix D §1.2, DEC-066): the amount is text with exactly two decimals, e.g.
/// <c>{ "amount": "1250.00", "currency": "TRY" }</c>, so it never passes through a JavaScript double.
/// </summary>
public sealed partial record MoneyDto(string Amount, string Currency)
{
    public static MoneyDto From(Money money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return new MoneyDto(money.ToAmountString(), money.Currency);
    }

    /// <summary>Accepts <c>1250</c>, <c>1250.5</c> or <c>1250.50</c> (dot, no grouping, no sign); anything else is <c>Money.Invalid</c>.</summary>
    public Result<Money> ToMoney()
    {
        if (Amount is null || Currency is null || !AmountPattern().IsMatch(Amount))
        {
            return Result.Failure<Money>(Money.Invalid);
        }

        return Money.Create(decimal.Parse(Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), Currency);
    }

    // [0-9], not \d: \d also matches other scripts' digits (e.g. Arabic-Indic), which decimal.Parse rejects.
    [GeneratedRegex(@"^[0-9]{1,10}(\.[0-9]{1,2})?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AmountPattern();
}
