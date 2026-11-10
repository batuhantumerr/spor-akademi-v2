using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct BillingRulesId(Guid Value) : IEntityId<BillingRulesId>
{
    public static BillingRulesId From(Guid value) => new(value);
}

/// <summary>
/// One version of a school's fee rules (P2-F8, DEC-370): the sibling discount and the single charge on a group change,
/// from <see cref="EffectiveFrom"/> until the next version. Versioned like group fees (DEC-180): at most one version
/// per period, never changed or removed, and never defined for a past period, so a past month's bill never changes.
/// Without any version the fees are charged exactly as before (DEC-017, DEC-183).
/// </summary>
public sealed class BillingRules : AggregateRoot<BillingRulesId>, ITenantScoped
{
    public const int MinPercent = 0;
    public const int MaxPercent = 100;

    private BillingRules(BillingRulesId id, SchoolId tenantId, BillingRuleTerms terms)
        : base(id)
    {
        TenantId = tenantId;
        EffectiveFrom = terms.EffectiveFrom;
        SecondChildPercent = terms.SecondChildPercent;
        ThirdAndLaterChildPercent = terms.ThirdAndLaterChildPercent;
        SingleChargeOnGroupChange = terms.SingleChargeOnGroupChange;
    }

    // For EF Core materialization only.
    private BillingRules()
        : base(default)
    {
        EffectiveFrom = null!;
    }

    public SchoolId TenantId { get; private set; }

    public YearMonth EffectiveFrom { get; private set; }

    /// <summary>Discount (whole percent, 0–100) on every fee of the second-ranked sibling of a month; 0 is off.</summary>
    public int SecondChildPercent { get; private set; }

    /// <summary>Discount (whole percent, 0–100) on every fee of the third and later siblings of a month; 0 is off.</summary>
    public int ThirdAndLaterChildPercent { get; private set; }

    /// <summary>A month already charged by an enrollment that ended in it is not charged again by the next one (group change).</summary>
    public bool SingleChargeOnGroupChange { get; private set; }

    public BillingRuleTerms Terms => new(EffectiveFrom, SecondChildPercent, ThirdAndLaterChildPercent, SingleChargeOnGroupChange);

    public static bool IsValidPercent(int percent) => percent is >= MinPercent and <= MaxPercent;

    /// <summary>
    /// A new version from <paramref name="terms"/>' period. Like a fee (<c>Fee.PeriodInPast</c>): the current period and
    /// later ones may be defined, earlier ones may not. "Already defined" is checked by the caller against the stored
    /// versions and by a unique index.
    /// </summary>
    public static Result<BillingRules> Define(SchoolId tenantId, BillingRuleTerms terms, YearMonth currentPeriod)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(currentPeriod);

        if (!IsValidPercent(terms.SecondChildPercent) || !IsValidPercent(terms.ThirdAndLaterChildPercent))
        {
            return Result.Failure<BillingRules>(BillingRulesErrors.InvalidPercent);
        }

        if (terms.EffectiveFrom < currentPeriod)
        {
            return Result.Failure<BillingRules>(BillingRulesErrors.PeriodInPast);
        }

        return Result.Success(new BillingRules(EntityId.New<BillingRulesId>(), tenantId, terms));
    }
}

/// <summary>The terms of one rules version, as the calculator uses them.</summary>
public sealed record BillingRuleTerms(YearMonth EffectiveFrom, int SecondChildPercent, int ThirdAndLaterChildPercent, bool SingleChargeOnGroupChange)
{
    /// <summary>The version in force in <paramref name="period"/>: the latest one not after it, or null (no rules).</summary>
    public static BillingRuleTerms? For(IEnumerable<BillingRuleTerms> versions, YearMonth period)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(period);
        return versions.Where(v => v.EffectiveFrom <= period).MaxBy(v => v.EffectiveFrom);
    }

    /// <summary>The discount of the sibling ranked <paramref name="rank"/> (1 = pays in full).</summary>
    public int PercentForRank(int rank) => rank switch
    {
        <= 1 => 0,
        2 => SecondChildPercent,
        _ => ThirdAndLaterChildPercent,
    };
}

public static class BillingRulesErrors
{
    public static readonly Error PeriodInPast = Error.BusinessRule("BillingRules.PeriodInPast", "A past period's fee rules cannot change.");
    public static readonly Error AlreadyDefined = Error.Conflict("BillingRules.AlreadyDefined", "Fee rules are already defined for this period.");
    public static readonly Error InvalidPercent = Error.Validation("BillingRules.InvalidPercent", "A discount is a whole percent from 0 to 100.");
}
