using System.Net;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Health;

public sealed class HealthTests(DatabaseFixture database) : IntegrationTest(database)
{
    [Fact]
    public async Task Ready_is_200_when_the_database_is_reachable()
    {
        var response = await Client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_is_503_and_live_is_200_when_the_database_is_unreachable()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2");
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
