using NetArchTest.Rules;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.ArchitectureTests;

public sealed class ModuleRulesTests
{
    public static readonly string[] Modules =
        ["Tenancy", "Identity", "Organization", "Groups", "Students", "Enrollments", "Attendance", "Billing", "Announcements"];

    /// <summary>Modules without a tenant: schools themselves, and users (the Platform Admin has no school).</summary>
    private static readonly string[] ModulesWithoutTenantFilter = ["Tenancy", "Identity"];

    public static TheoryData<string> ModuleNames() => [.. Modules];

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Application_modules_only_use_other_modules_through_contracts(string module)
    {
        // Every type of another module except its public Contracts is off limits.
        var forbidden = Assemblies.Application.GetTypes()
            .Where(t => t.Namespace is { } ns
                && Modules.Where(m => m != module).Any(m => ns.StartsWith($"SporAkademi.Application.{m}", StringComparison.Ordinal))
                && !ns.Contains(".Contracts", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Distinct()
            .ToArray();
        if (forbidden.Length == 0)
        {
            return;
        }

        var result = Types.InAssembly(Assemblies.Application)
            .That().ResideInNamespaceStartingWith($"SporAkademi.Application.{module}")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    [Trait("Story", "US-02")]
    public void Aggregates_of_school_modules_are_tenant_scoped()
    {
        var missing = Assemblies.Domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && IsAggregateRoot(t))
            .Where(t => !ModulesWithoutTenantFilter.Any(m => t.Namespace!.StartsWith($"SporAkademi.Domain.{m}", StringComparison.Ordinal)))
            .Where(t => !typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Ignore_query_filters_is_only_used_by_tenancy()
    {
        var offenders = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Where(file => !file.Replace('\\', '/').Contains("/Tenancy/", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Business_code_never_reads_the_system_clock()
    {
        var offenders = SourceFiles()
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("DateTime.Now", StringComparison.Ordinal)
                    || text.Contains("DateTime.UtcNow", StringComparison.Ordinal)
                    || text.Contains("DateTimeOffset.Now", StringComparison.Ordinal)
                    || text.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal);
            })
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Handlers_are_internal_and_sealed()
    {
        var handlerInterfaces = new[] { "ICommandHandler`2", "IQueryHandler`2", "IDomainEventConsumer`1" };
        var offenders = Assemblies.Application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Namespace?.StartsWith("SporAkademi.Application.Abstractions", StringComparison.Ordinal) != true)
            .Where(t => t.GetInterfaces().Any(i => handlerInterfaces.Contains(i.Name)))
            .Where(t => t.IsPublic || !t.IsSealed)
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static bool IsAggregateRoot(Type type) =>
        typeof(IAggregateRoot).IsAssignableFrom(type);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
