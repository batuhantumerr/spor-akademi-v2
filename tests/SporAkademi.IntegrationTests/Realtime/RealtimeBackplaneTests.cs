using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Realtime;
using SporAkademi.IntegrationTests.Caching;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Realtime;

/// <summary>
/// P3-08 (DEC-401): with <c>ConnectionStrings:Cache</c> the Valkey backplane carries a notice from the API instance that
/// delivers the outbox message to a client connected to another instance; school and user still decide who gets it.
/// </summary>
[Trait("Story", "P3-08")]
public sealed class RealtimeBackplaneTests(DatabaseFixture database, ValkeyFixture valkey)
    : RealtimeTest(database), IClassFixture<ValkeyFixture>
{
    protected override ApiFactory CreateApi() => new(Database.AppConnectionString, valkey.Valkey.ConnectionString);

    [Fact]
    public async Task A_notice_sent_by_one_instance_reaches_a_client_of_another()
    {
        var coach = Guid.CreateVersion7();
        var otherSchool = Guid.CreateVersion7();
        var listener = await ListenAsync(TestTokens.Create(coach, "Coach", School.Value, Api.Clock.GetUtcNow()));
        var bystander = await ListenAsync(TestTokens.Create(coach, "Coach", otherSchool, Api.Clock.GetUtcNow()));

        await using var second = CreateApi();
        var notifier = second.Services.GetRequiredService<IRealtimeNotifier>();
        var id = Guid.CreateVersion7();
        await notifier.NotifyUsersAsync(School.Value, [coach], new RealtimeEvent(RealtimeEventTypes.AnnouncementPublished, id, GroupId: Guid.CreateVersion7()), Ct);
        await notifier.NotifyRoleAsync(School.Value, Role.Coach, new RealtimeEvent(RealtimeEventTypes.AnnouncementPublished, id), Ct);

        var notice = await listener.WaitForAsync(OfType("announcement.published"), Arrives);
        Text(notice, "id").ShouldBe(id.ToString());
        await Task.Delay(StaysQuiet, Ct);
        listener.Received.Count.ShouldBe(2);
        bystander.Received.ShouldBeEmpty();
    }
}
