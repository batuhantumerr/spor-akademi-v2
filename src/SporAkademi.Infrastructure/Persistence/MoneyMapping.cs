using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>Money columns: <c>amount numeric(12,2)</c> (never a float, US-23) and <c>currency varchar(3)</c>.</summary>
internal static class MoneyMapping
{
    public static void Configure(ComplexPropertyBuilder<Money> money) => Configure(money, "amount", "currency");

    public static void Configure(ComplexPropertyBuilder<Money> money, string amountColumn, string currencyColumn)
    {
        ArgumentNullException.ThrowIfNull(money);
        money.Property(m => m.Amount).HasColumnName(amountColumn).HasPrecision(12, 2);
        money.Property(m => m.Currency).HasColumnName(currencyColumn).HasMaxLength(3);
    }
}
