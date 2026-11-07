using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Tenancy;

/// <summary>
/// How long a school keeps data (DEC-008): personal data of departed students is anonymized after
/// <see cref="PersonalDataMonths"/>; payments are kept for <see cref="PaymentYears"/>.
/// </summary>
public sealed record RetentionPolicy
{
    /// <summary>
    /// Bounds a School Admin may choose (DEC-361): under six months a student marked "left" by mistake could lose
    /// their data before anyone notices; over ten years the data is no longer needed for any school purpose.
    /// </summary>
    public const int MinPersonalDataMonths = 6;

    public const int MaxPersonalDataMonths = 120;

    public const int MinPaymentYears = 1;

    public const int MaxPaymentYears = 50;

    public static readonly Error Invalid = Error.Validation("RetentionPolicy.Invalid", "Retention periods are out of range.");

    /// <summary>Defaults (DEC-360, Proposed): two years of personal data, ten years of payment records (Turkish commercial records).</summary>
    public static readonly RetentionPolicy Default = new(24, 10);

    private RetentionPolicy(int personalDataMonths, int paymentYears)
    {
        PersonalDataMonths = personalDataMonths;
        PaymentYears = paymentYears;
    }

    // For EF Core materialization only (records have a copy constructor EF cannot bind).
    private RetentionPolicy()
    {
    }

    public int PersonalDataMonths { get; private init; }

    public int PaymentYears { get; private init; }

    public static Result<RetentionPolicy> Create(int personalDataMonths, int paymentYears) =>
        personalDataMonths is >= MinPersonalDataMonths and <= MaxPersonalDataMonths
        && paymentYears is >= MinPaymentYears and <= MaxPaymentYears
            ? Result.Success(new RetentionPolicy(personalDataMonths, paymentYears))
            : Result.Failure<RetentionPolicy>(Invalid);

    /// <summary>
    /// The latest leave date whose personal data is due for anonymization on <paramref name="today"/> (DEC-362): a
    /// student who left on or before it is anonymized. Month arithmetic clamps to the month's end (31 March minus one
    /// month is 28 or 29 February), so a period never ends early.
    /// </summary>
    public DateOnly AnonymizationCutoff(DateOnly today) => today.AddMonths(-PersonalDataMonths);
}
