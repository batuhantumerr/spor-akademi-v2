using System.Reflection;
using Hangfire;

namespace SporAkademi.ArchitectureTests;

/// <summary>P2-F2 (DEC-333, DEC-334): what a background job may carry and how it reaches school data.</summary>
public sealed class JobsRulesTests
{
    /// <summary>Job arguments are stored and shown in the panel: ids and counts at most, never personal data.</summary>
    private static readonly Type[] AllowedParameterTypes = [typeof(CancellationToken), typeof(Guid), typeof(int)];

    private static readonly List<MethodInfo> JobMethods =
    [
        .. Assemblies.Infrastructure.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<JobDisplayNameAttribute>() is not null),
    ];

    [Fact]
    public void Jobs_take_only_ids_and_a_cancellation_token()
    {
        JobMethods.ShouldNotBeEmpty();
        var offenders = JobMethods
            .Where(m => m.GetParameters().Any(p => !AllowedParameterTypes.Contains(p.ParameterType)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Jobs_are_not_retried_automatically()
    {
        // A recurring run that failed is followed by the next occurrence; automatic retries would pile up.
        var offenders = JobMethods
            .Where(m => m.GetCustomAttribute<AutomaticRetryAttribute>() is not { Attempts: 0 })
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_tenant_job_runner_enters_a_background_tenant_in_jobs()
    {
        var jobsFolder = Path.Combine(RepositoryRoot(), "src", "SporAkademi.Infrastructure", "Jobs");
        var offenders = Directory.EnumerateFiles(jobsFolder, "*.cs")
            .Where(f => !f.EndsWith("TenantJobRunner.cs", StringComparison.Ordinal)
                && File.ReadAllText(f).Contains("BackgroundTenantContext", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SporAkademi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
