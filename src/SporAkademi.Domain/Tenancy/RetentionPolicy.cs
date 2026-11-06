using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Tenancy;

/// <summary>
/// How long a school keeps data (DEC-008): personal data of departed students is anonymized after
/// <see cref="PersonalDataMonths"/>; payments are kept for <see cref="PaymentYears"/>.
/// </summary>
public sealed record RetentionPolicy
{
    public static readonly Error Invalid = Error.Validation("RetentionPolicy.Invalid", "Retention periods must be positive.");

    /// <summary>Defaults: two years of personal data, ten years of payment records (Turkish commercial records).</summary>
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
        personalDataMonths is > 0 and <= 240 && paymentYears is > 0 and <= 50
            ? Result.Success(new RetentionPolicy(personalDataMonths, paymentYears))
            : Result.Failure<RetentionPolicy>(Invalid);
}
