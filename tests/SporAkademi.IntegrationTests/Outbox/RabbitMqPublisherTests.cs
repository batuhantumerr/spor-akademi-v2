using SporAkademi.Infrastructure.Messaging;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>P5-03: what the publisher puts on the wire, read from the queue (this host runs no consumers).</summary>
[Trait("Story", "P5-03")]
public sealed class RabbitMqPublisherTests(DatabaseFixture database) : RabbitMqOutboxTest(database)
{
    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["RabbitMq:Consumers:Enabled"] = "false";
            return settings;
        }
    }

    [Fact]
    public async Task A_message_of_a_school_always_carries_its_tenant_header_and_a_platform_message_none()
    {
        var school = Guid.CreateVersion7();
        await EnqueueAsync(tenant: school);
        await ProcessAsync();

        var withTenant = (await TakeAsync(ProbeQueue)).ShouldNotBeNull();
        RabbitMqBroker.HeaderString(withTenant.BasicProperties.Headers, RabbitMqBroker.TenantHeader).ShouldBe(school.ToString());
        withTenant.BasicProperties.Persistent.ShouldBeTrue();
        RabbitMqBroker.HeaderString(withTenant.BasicProperties.Headers, RabbitMqBroker.AttemptHeader).ShouldBe("1");

        await EnqueueAsync(tenant: null);
        await ProcessAsync();

        var platform = (await TakeAsync(ProbeQueue)).ShouldNotBeNull();
        RabbitMqBroker.HeaderString(platform.BasicProperties.Headers, RabbitMqBroker.TenantHeader).ShouldBeNull();
    }
}
