using System.Globalization;

namespace SporAkademi.Domain.SharedKernel;

/// <summary>
/// A non-negative TRY amount with at most two decimals and no rounding, bounded by <c>decimal(12,2)</c>.
/// Mixing currencies is a programmer error.
/// </summary>
public sealed record Money : IComparable<Money>
{
    public const string TryCurrency = "TRY";
    public const decimal MaxAmount = 9_999_999_999.99m;

    public static readonly Error Invalid = Error.Validation("Money.Invalid", "The amount is not valid.");

    public static readonly Money Zero = new(0m, TryCurrency);

    private Money(decimal amount, string currency)
    {
        // Normalize the scale to two decimals so 1250 and 1250.00 are equal and hash alike.
        Amount = decimal.Round(amount + 0.00m, 2);
        Currency = currency;
    }

    // For EF Core materialization only (records have a copy constructor EF cannot bind).
    private Money()
    {
        Currency = TryCurrency;
    }

    public decimal Amount { get; private init; }

    public string Currency { get; private init; }

    public static Result<Money> Create(decimal amount, string currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (currency != TryCurrency || amount < 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
        {
            return Result.Failure<Money>(Invalid);
        }

        return Result.Success(new Money(amount, currency));
    }

    /// <summary>Creates a TRY amount that the caller knows is valid; throws otherwise.</summary>
    public static Money Try(decimal amount)
    {
        var result = Create(amount, TryCurrency);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentOutOfRangeException(nameof(amount), amount, "Not a valid TRY amount.");
    }

    /// <summary>Adds; exceeding <see cref="MaxAmount"/> is a programmer error.</summary>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        var sum = Amount + other.Amount;
        return sum <= MaxAmount
            ? Try(sum)
            : throw new InvalidOperationException("Money exceeds the supported maximum.");
    }

    /// <summary>Subtracts; a negative result is a programmer error (callers compare first).</summary>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        var result = Amount - other.Amount;
        return result >= 0
            ? Try(result)
            : throw new InvalidOperationException("Money cannot become negative.");
    }

    /// <summary>Amount as invariant text with exactly two decimals, e.g. <c>1250.00</c> (API format).</summary>
    public string ToAmountString() => Amount.ToString("0.00", CultureInfo.InvariantCulture);

    public int CompareTo(Money? other)
    {
        if (other is null)
        {
            return 1;
        }

        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    public override string ToString() => $"{ToAmountString()} {Currency}";

    private static int Compare(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.CompareTo(right);
    }

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Currency != Currency)
        {
            throw new InvalidOperationException($"Cannot combine {Currency} and {other.Currency}.");
        }
    }
}
