using NSubstitute;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Organization;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.UnitTests.Organization;

public sealed class DeleteRulesTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();

    [Fact]
    [Trait("Story", "US-06")]
    public async Task Sport_with_groups_cannot_be_deleted()
    {
        var sport = Sport.Create(School, "Futbol").Value;
        var sports = Substitute.For<ISportRepository>();
        sports.GetAsync(sport.Id, Arg.Any<CancellationToken>()).Returns(sport);
        var groups = Substitute.For<IGroupUsage>();
        groups.AnyGroupUsesSportAsync(sport.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await new DeleteSportHandler(sports, groups).Handle(new DeleteSportCommand(sport.Id), CancellationToken.None);

        result.Error.ShouldBe(SportErrors.InUse);
        sports.DidNotReceive().Remove(Arg.Any<Sport>());
    }

    [Fact]
    [Trait("Story", "US-07")]
    public async Task Facility_used_by_an_active_group_cannot_be_deleted()
    {
        var facility = Facility.Create(School, "A Sahası", FacilityType.Field, Address.Create("Cad. 1", "Kadıköy", "İstanbul").Value, null).Value;
        var facilities = Substitute.For<IFacilityRepository>();
        facilities.GetAsync(facility.Id, Arg.Any<CancellationToken>()).Returns(facility);
        var groups = Substitute.For<IGroupUsage>();
        groups.AnyActiveGroupUsesFacilityAsync(facility.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await new DeleteFacilityHandler(facilities, groups).Handle(new DeleteFacilityCommand(facility.Id), CancellationToken.None);

        result.Error.ShouldBe(FacilityErrors.InUse);
        facilities.DidNotReceive().Remove(Arg.Any<Facility>());
    }
}
