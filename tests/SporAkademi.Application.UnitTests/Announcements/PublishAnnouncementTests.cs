using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Announcements;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.UnitTests.Announcements;

[Trait("Story", "US-25")]
public sealed class PublishAnnouncementTests
{
    private static readonly GroupId Group = EntityId.New<GroupId>();

    [Theory]
    [InlineData(" ", "Metin", "title", "Field.Required")]
    [InlineData("Başlık", " ", "body", "Field.Required")]
    [InlineData("long-title", "Metin", "title", "Field.TooLong")]
    [InlineData("Başlık", "long-body", "body", "Field.TooLong")]
    public async Task Domain_failures_are_reported_on_the_failing_field(string title, string body, string field, string code)
    {
        title = title == "long-title" ? new string('a', Announcement.TitleMaxLength + 1) : title;
        body = body == "long-body" ? new string('b', Announcement.BodyMaxLength + 1) : body;
        var groups = Substitute.For<IGroupDirectory>();
        groups.FindAsync(Group, Arg.Any<CancellationToken>())
            .Returns(new GroupInfo(Group.Value, "U10", GroupStatus.Active, BirthYearRange.Create(2015, 2016).Value, 20, null, []));
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.CreateVersion7());
        user.TenantId.Returns(Guid.CreateVersion7());
        var announcements = Substitute.For<IAnnouncementRepository>();
        var handler = new PublishAnnouncementHandler(announcements, groups, user, new FakeTimeProvider());

        // Called without the validator, as a backstop.
        var result = await handler.Handle(new PublishAnnouncementCommand(Group, title, body), CancellationToken.None);

        var errors = (IReadOnlyDictionary<string, string[]>)result.Error.Arguments[ValidationErrors.ErrorsArgument]!;
        errors.Keys.ShouldBe([field]);
        errors[field].ShouldBe([code]);
        announcements.DidNotReceive().Add(Arg.Any<Announcement>());
    }

    [Fact]
    public void Validator_counts_lengths_after_normalisation()
    {
        var validator = new PublishAnnouncementValidator();
        var paddedTitle = "  " + string.Join("   ", Enumerable.Repeat("abcd", 30)) + "  ";

        validator.Validate(new PublishAnnouncementCommand(Group, paddedTitle, "\n  " + new string('b', 4000) + "  \n")).IsValid.ShouldBeTrue();
        var invalid = validator.Validate(new PublishAnnouncementCommand(Group, " \t ", new string('b', 4001)));
        invalid.Errors.Select(e => (e.PropertyName, e.ErrorCode)).ShouldBe([("title", "Field.Required"), ("body", "Field.TooLong")]);
    }
}
