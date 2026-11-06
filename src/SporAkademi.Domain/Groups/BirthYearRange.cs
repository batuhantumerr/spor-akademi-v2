using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Groups;

/// <summary>
/// The birth years a group accepts, both ends inclusive ("2015–2016 doğumlular", US-09). Used instead of
/// an age range so a student never ages out of a group in the middle of a season.
/// </summary>
public sealed record BirthYearRange
{
    public const int MinYear = 1900;
    public const int MaxYear = 2100;

    public static readonly Error Invalid = Error.Validation("BirthYearRange.Invalid", "From must not be after To; years 1900-2100.");

    private BirthYearRange(int from, int to)
    {
        From = from;
        To = to;
    }

    // For EF Core materialization only (records have a copy constructor EF cannot bind).
    private BirthYearRange()
    {
    }

    public int From { get; private init; }

    public int To { get; private init; }

    public static Result<BirthYearRange> Create(int from, int to) =>
        from is >= MinYear and <= MaxYear && to is >= MinYear and <= MaxYear && from <= to
            ? Result.Success(new BirthYearRange(from, to))
            : Result.Failure<BirthYearRange>(Invalid);

    public bool Contains(int birthYear) => birthYear >= From && birthYear <= To;
}
