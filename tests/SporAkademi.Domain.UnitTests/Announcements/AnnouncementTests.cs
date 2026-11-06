using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Announcements;

[Trait("Story", "US-25")]
public sealed class AnnouncementTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly GroupId Group = EntityId.New<GroupId>();
    private static readonly UserId Admin = EntityId.New<UserId>();
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    private static Result<Announcement> Publish(string title, string body) => Announcement.Publish(School, Group, Admin, title, body, Now);

    [Fact]
    public void Announcement_keeps_plain_text_author_and_time()
    {
        var announcement = Publish("  Cumartesi   antrenmanı ", "Saat 10:00'a alındı.\n<b>Yağmur</b> olursa iptal.\n").Value;

        announcement.Title.ShouldBe("Cumartesi antrenmanı");
        announcement.Body.ShouldBe("Saat 10:00'a alındı.\n<b>Yağmur</b> olursa iptal.");
        announcement.AuthorId.ShouldBe(Admin);
        announcement.PublishedAt.ShouldBe(Now);
        announcement.GroupId.ShouldBe(Group);
        announcement.TenantId.ShouldBe(School);
    }

    [Theory]
    [InlineData(" ", "Metin")]
    [InlineData("Başlık", "  ")]
    public void Title_and_body_are_required(string title, string body)
    {
        Publish(title, body).Error.ShouldBe(CommonErrors.FieldRequired);
    }

    [Fact]
    public void Title_and_body_are_bounded()
    {
        Publish(new string('a', 150), new string('b', 4000)).IsSuccess.ShouldBeTrue();
        Publish(new string('a', 151), "Metin").Error.ShouldBe(CommonErrors.FieldTooLong);
        Publish("Başlık", new string('b', 4001)).Error.ShouldBe(CommonErrors.FieldTooLong);
    }
}
