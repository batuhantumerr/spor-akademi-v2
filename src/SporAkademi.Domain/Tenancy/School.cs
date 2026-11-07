using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Tenancy;

public readonly record struct SchoolId(Guid Value) : IEntityId<SchoolId>
{
    public static SchoolId From(Guid value) => new(value);
}

public enum SchoolStatus
{
    Active,
    Deactivated,
}

/// <summary>A sports school; its id is the <c>TenantId</c> everywhere else. Not tenant-scoped itself.</summary>
public sealed class School : AggregateRoot<SchoolId>
{
    public const int NameMaxLength = 150;
    public const int TimeZoneMaxLength = 64;

    private School(SchoolId id, string name, Slug slug, string timeZone, RetentionPolicy retentionPolicy)
        : base(id)
    {
        Name = name;
        Slug = slug;
        TimeZone = timeZone;
        RetentionPolicy = retentionPolicy;
        Status = SchoolStatus.Active;
    }

    // For EF Core materialization only.
    private School()
        : base(default)
    {
        Name = null!;
        Slug = null!;
        TimeZone = null!;
        RetentionPolicy = null!;
    }

    public string Name { get; private set; }

    public Slug Slug { get; private set; }

    /// <summary>IANA time zone id, e.g. <c>Europe/Istanbul</c>. Local dates and times are interpreted in it.</summary>
    public string TimeZone { get; private set; }

    public SchoolStatus Status { get; private set; }

    public RetentionPolicy RetentionPolicy { get; private set; }

    public static Result<School> Create(string name, Slug slug, string timeZone, RetentionPolicy retentionPolicy)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(retentionPolicy);

        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
        {
            return Result.Failure<School>(CommonErrors.FieldRequired);
        }

        if (trimmedName.Length > NameMaxLength)
        {
            return Result.Failure<School>(CommonErrors.FieldTooLong);
        }

        if (!IsKnownTimeZone(timeZone))
        {
            return Result.Failure<School>(SchoolErrors.InvalidTimeZone);
        }

        return Result.Success(new School(EntityId.New<SchoolId>(), trimmedName, slug, timeZone, retentionPolicy));
    }

    /// <summary>
    /// The School Admin's setting (P2-F5, DEC-361): only the personal data period changes; the payment period is a
    /// legal minimum and stays as it is.
    /// </summary>
    public Result ChangePersonalDataRetention(int personalDataMonths)
    {
        var policy = RetentionPolicy.Create(personalDataMonths, RetentionPolicy.PaymentYears);
        if (policy.IsFailure)
        {
            return Result.Failure(policy.Error);
        }

        RetentionPolicy = policy.Value;
        return Result.Success();
    }

    /// <summary>Today's date in the school's time zone.</summary>
    public DateOnly Today(TimeProvider timeProvider) => DateOnly.FromDateTime(LocalNow(timeProvider));

    /// <summary>The wall-clock time now in the school's time zone, comparable with local dates and schedule times.</summary>
    public DateTime LocalNow(TimeProvider timeProvider) => LocalNowIn(TimeZone, timeProvider);

    /// <summary>The wall-clock time now in <paramref name="timeZone"/>, for callers that only hold a school's time zone.</summary>
    public static DateTime LocalNowIn(string timeZone, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime;
    }

    public static bool IsKnownTimeZone(string timeZone) =>
        timeZone.Length is > 0 and <= TimeZoneMaxLength
        && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone)
        && zone.HasIanaId;
}

public static class SchoolErrors
{
    public static readonly Error SlugTaken = Error.Conflict("School.SlugTaken", "The slug is already used by another school.");
    public static readonly Error InvalidTimeZone = Error.Validation("School.InvalidTimeZone", "Unknown time zone.");
    public static readonly Error NotFound = Error.NotFound("School.NotFound", "School not found.");
}
