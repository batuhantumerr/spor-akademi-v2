using NSubstitute;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Groups;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.UnitTests.Groups;

[Trait("Story", "US-08")]
public sealed class CoachDeactivationTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly UserId Coach = EntityId.New<UserId>();

    private static ICurrentUser UserOf(Guid? tenant)
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(tenant);
        return user;
    }

    [Fact]
    public async Task Groups_of_the_deactivated_coach_lose_the_coach()
    {
        var group = Group.Create(School, EntityId.New<SportId>(), EntityId.New<FacilityId>(), Coach, "U10", BirthYearRange.Create(2015, 2016).Value, 10).Value;
        var groups = Substitute.For<IGroupRepository>();
        groups.ListByCoachAsync(Coach, Arg.Any<CancellationToken>()).Returns([group]);

        var result = await new ClearCoachWhenUserDeactivated(groups, UserOf(School.Value)).Handle(new UserDeactivated(Coach, School), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        group.CoachId.ShouldBeNull();
    }

    [Fact]
    public async Task Deactivation_outside_the_users_school_fails_loudly()
    {
        var groups = Substitute.For<IGroupRepository>();

        foreach (var caller in new Guid?[] { Guid.CreateVersion7(), null })
        {
            var consumer = new ClearCoachWhenUserDeactivated(groups, UserOf(caller));

            await Should.ThrowAsync<InvalidOperationException>(() => consumer.Handle(new UserDeactivated(Coach, School), CancellationToken.None));
        }

        await groups.DidNotReceive().ListByCoachAsync(Arg.Any<UserId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Users_without_a_school_need_nothing()
    {
        var groups = Substitute.For<IGroupRepository>();

        var result = await new ClearCoachWhenUserDeactivated(groups, UserOf(null)).Handle(new UserDeactivated(Coach, null), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await groups.DidNotReceive().ListByCoachAsync(Arg.Any<UserId>(), Arg.Any<CancellationToken>());
    }
}
