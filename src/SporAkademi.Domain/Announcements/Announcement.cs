using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Announcements;

public readonly record struct AnnouncementId(Guid Value) : IEntityId<AnnouncementId>
{
    public static AnnouncementId From(Guid value) => new(value);
}

/// <summary>
/// A message from the school to a group's coach (US-25, appendix B 4.11). Plain text, never edited.
/// The author and time are domain data (<see cref="AuthorId"/>, <see cref="PublishedAt"/>), separate from
/// the technical audit columns every table has (DEC-190).
/// </summary>
public sealed class Announcement : AggregateRoot<AnnouncementId>, ITenantScoped
{
    public const int TitleMaxLength = 150;
    public const int BodyMaxLength = 4000;

    private Announcement(AnnouncementId id, SchoolId tenantId, GroupId groupId, UserId authorId, string title, string body, DateTimeOffset publishedAt)
        : base(id)
    {
        TenantId = tenantId;
        GroupId = groupId;
        AuthorId = authorId;
        Title = title;
        Body = body;
        PublishedAt = publishedAt;
    }

    // For EF Core materialization only.
    private Announcement()
        : base(default)
    {
        Title = null!;
        Body = null!;
    }

    public SchoolId TenantId { get; private set; }

    public GroupId GroupId { get; private set; }

    /// <summary>The School Admin who published it ("CreatedBy" of appendix B).</summary>
    public UserId AuthorId { get; private set; }

    public string Title { get; private set; }

    /// <summary>Plain text; line breaks are kept, nothing is interpreted as markup.</summary>
    public string Body { get; private set; }

    /// <summary>"CreatedAt" of appendix B.</summary>
    public DateTimeOffset PublishedAt { get; private set; }

    public static Result<Announcement> Publish(
        SchoolId tenantId,
        GroupId groupId,
        UserId authorId,
        string title,
        string body,
        DateTimeOffset now)
    {
        var cleanTitle = CleanTitle(title);
        if (cleanTitle.IsFailure)
        {
            return Result.Failure<Announcement>(cleanTitle.Error);
        }

        var cleanBody = CleanBody(body);
        if (cleanBody.IsFailure)
        {
            return Result.Failure<Announcement>(cleanBody.Error);
        }

        return Result.Success(new Announcement(EntityId.New<AnnouncementId>(), tenantId, groupId, authorId, cleanTitle.Value, cleanBody.Value, now));
    }

    /// <summary>The stored title: whitespace runs collapsed to single spaces; required, at most <see cref="TitleMaxLength"/> after that.</summary>
    public static Result<string> CleanTitle(string? title) => Bounded(TextNormalization.CollapseSpaces(title ?? string.Empty), TitleMaxLength);

    /// <summary>The stored body: only trimmed, inner line breaks kept; required, at most <see cref="BodyMaxLength"/> after that.</summary>
    public static Result<string> CleanBody(string? body) => Bounded((body ?? string.Empty).Trim(), BodyMaxLength);

    private static Result<string> Bounded(string value, int maxLength) => value.Length switch
    {
        0 => Result.Failure<string>(CommonErrors.FieldRequired),
        var length when length > maxLength => Result.Failure<string>(CommonErrors.FieldTooLong),
        _ => Result.Success(value),
    };
}
