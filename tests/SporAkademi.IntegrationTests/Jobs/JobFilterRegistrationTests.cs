using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;

namespace SporAkademi.IntegrationTests.Jobs;

/// <summary>
/// Hangfire keeps job filters in a process-wide static list. Every host runs the Hangfire configuration callback, so
/// the filters must be added once per process, not once per host: a test run builds hundreds of hosts, and a stacked
/// filter chain overflowed the stack inside <c>BackgroundJobPerformer</c>.
/// </summary>
[Trait("Story", "P4-07")]
public sealed class JobFilterRegistrationTests
{
    [Fact]
    public void Building_several_hosts_keeps_one_instance_of_each_job_filter()
    {
        for (var i = 0; i < 3; i++)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:App"] = "Host=localhost;Database=unused;Username=unused",
                })
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddJobs();
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<IGlobalConfiguration>();
        }

        GlobalJobFilters.Filters.Count(f => f.Instance is JobActivityFilter).ShouldBe(1);
        GlobalJobFilters.Filters.Count(f => f.Instance is FailedJobExceptionFilter).ShouldBe(1);
    }
}
