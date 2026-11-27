namespace SporAkademi.ArchitectureTests;

/// <summary>
/// V1-02 (DEC-682, closes the gaps of docs/v1-mimari-inceleme.md section 2): module boundaries for every Application folder
/// (including Audit and Realtime) and for the Domain and Infrastructure sides. Where the code crosses on purpose (DEC-660) the
/// crossing is a named allowlist entry below, not a weaker rule.
/// </summary>
[Trait("Story", "V1-02")]
public sealed class ModuleBoundaryRulesTests
{
    /// <summary>Folders that are shared kernel, not modules: every module may reference them.</summary>
    private static readonly string[] SharedKernel = ["SharedKernel", "Tenancy"];

    /// <summary>
    /// Domain modules may name another module's Domain namespace only along these edges (strongly typed IDs and the small
    /// published records such as PaidEnrollment). A new edge is a design decision: add it here with the reason in the PR.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedDomainEdges = new()
    {
        ["Announcements"] = ["Groups", "Identity"],
        ["Attendance"] = ["Groups", "Identity", "Students"],
        ["Billing"] = ["Enrollments", "Groups", "Identity", "Students"],
        ["Enrollments"] = ["Groups", "Students"],
        ["Groups"] = ["Identity", "Organization"],
        ["Students"] = ["Identity"],
    };

    /// <summary>The same relationship on the persistence side: per-module Infrastructure code that maps the FKs above.</summary>
    private static readonly Dictionary<string, string[]> AllowedInfrastructureDomainEdges = new()
    {
        ["Announcements"] = ["Groups"],
        ["Attendance"] = ["Groups", "Students"],
        ["Billing"] = ["Enrollments", "Groups", "Students"],
        ["Enrollments"] = ["Groups", "Students"],
        ["Groups"] = ["Identity", "Organization"],
    };

    /// <summary>
    /// DEC-660: read models and cross-cutting modules that read other modules' tables through <c>AppDbContext</c> by design
    /// (folder name, or a single file when it ends in <c>.cs</c>).
    /// </summary>
    private static readonly string[] CrossModuleReaders =
    [
        "Dashboard", "Reports", "Audit",
        "Students/StudentDataExportReadModel.cs", "Students/ParentChildren.cs", "Students/StudentParentAccounts.cs",
    ];

    /// <summary>Infrastructure code that names another module's Application types outside <c>Contracts</c> (reason in brackets).</summary>
    private static readonly string[] InfrastructureApplicationExceptions =
    [
        "Tenancy/CheckoutTenantResolver.cs", // payment webhook resolves the school through Billing's checkout lookup (DEC-523)
    ];

    /// <summary>
    /// Small immutable snapshots (ids, status, value objects of a student and a group) that the enrollment policy decides on, so
    /// the Enrollments domain never loads the other aggregates. They are the one place an ID-only rule is relaxed.
    /// </summary>
    private static readonly string[] PublishedSnapshots =
    [
        "SporAkademi.Domain.Enrollments.EnrollmentStudent",
        "SporAkademi.Domain.Enrollments.EnrollmentGroup",
    ];

    public static TheoryData<string> ApplicationModuleNames() => [.. ApplicationModules()];

    [Theory]
    [MemberData(nameof(ApplicationModuleNames))]
    public void Application_folders_only_use_other_modules_through_contracts(string module)
    {
        // Same rule as ModuleRulesTests, but the module list comes from the folders (so Audit and Realtime are covered too).
        var forbidden = Assemblies.Application.GetTypes()
            .Where(t => t.Namespace is { } ns
                && ApplicationModules().Where(m => m != module).Any(m => ns == $"SporAkademi.Application.{m}" || ns.StartsWith($"SporAkademi.Application.{m}.", StringComparison.Ordinal))
                && !ns.Contains(".Contracts", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Distinct()
            .ToArray();
        forbidden.ShouldNotBeEmpty();

        var result = NetArchTest.Rules.Types.InAssembly(Assemblies.Application)
            .That().ResideInNamespaceStartingWith($"SporAkademi.Application.{module}")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Application_module_list_includes_audit_and_realtime()
    {
        ApplicationModules().ShouldContain("Audit");
        ApplicationModules().ShouldContain("Realtime");
    }

    [Fact]
    public void Domain_modules_reference_each_other_only_along_allowed_edges()
    {
        var offenders = DomainEdges()
            .Where(edge => !AllowedDomainEdges.TryGetValue(edge.From, out var allowed) || !allowed.Contains(edge.To))
            .Select(edge => $"{edge.From} -> {edge.To}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Domain_module_edges_are_acyclic()
    {
        var graph = DomainEdges().GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());
        var cycles = new List<string>();
        var done = new HashSet<string>();

        void Visit(string node, List<string> path)
        {
            if (path.Contains(node))
            {
                cycles.Add(string.Join(" -> ", path.SkipWhile(p => p != node).Append(node)));
                return;
            }

            if (!done.Add(node) && !path.Contains(node))
            {
                return;
            }

            path.Add(node);
            foreach (var next in graph.GetValueOrDefault(node, []))
            {
                Visit(next, path);
            }

            path.RemoveAt(path.Count - 1);
        }

        foreach (var node in graph.Keys)
        {
            Visit(node, []);
        }

        cycles.ShouldBeEmpty();
    }

    [Fact]
    public void Domain_modules_hold_other_modules_only_as_strongly_typed_ids()
    {
        // Entities of different modules never point at each other: a field or property of another module's type must be its ID.
        var offenders = new List<string>();
        foreach (var type in Assemblies.Domain.GetTypes().Where(t => t.Namespace is not null && !t.IsNested))
        {
            var module = DomainModuleOf(type.Namespace!);
            if (module is null || SharedKernel.Contains(module) || PublishedSnapshots.Contains(type.FullName))
            {
                continue;
            }

            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            var memberTypes = type.GetProperties(all).Select(p => p.PropertyType)
                .Concat(type.GetFields(all).Where(f => !f.Name.Contains('<', StringComparison.Ordinal)).Select(f => f.FieldType));
            foreach (var memberType in memberTypes.SelectMany(Unwrap).Distinct())
            {
                var other = memberType.Namespace is { } ns ? DomainModuleOf(ns) : null;
                if (other is not null && other != module && !SharedKernel.Contains(other) && !memberType.Name.EndsWith("Id", StringComparison.Ordinal))
                {
                    offenders.Add($"{type.FullName} holds {memberType.FullName}");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Infrastructure_modules_name_other_modules_applications_only_through_contracts()
    {
        var modules = ApplicationModules();
        var offenders = RepositorySources.Files("SporAkademi.Infrastructure")
            .Where(file => RepositorySources.ModuleOf(file) is { } m && modules.Contains(m))
            .Where(file => !InfrastructureApplicationExceptions.Contains(file.RelativePath))
            .SelectMany(file => RepositorySources.References(file, "Application")
                .Where(r => modules.Contains(r.Module) && r.Module != RepositorySources.ModuleOf(file) && !r.ThroughContracts)
                .Select(r => $"{file.RelativePath} -> Application.{r.Module}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Infrastructure_modules_name_other_modules_domains_only_along_allowed_edges()
    {
        var modules = ApplicationModules();
        var offenders = RepositorySources.Files("SporAkademi.Infrastructure")
            .Where(file => RepositorySources.ModuleOf(file) is { } m && modules.Contains(m))
            .Where(file => !IsCrossModuleReader(file.RelativePath))
            .SelectMany(file => RepositorySources.References(file, "Domain")
                .Where(r => r.Module != RepositorySources.ModuleOf(file) && !SharedKernel.Contains(r.Module))
                .Where(r => !AllowedInfrastructureDomainEdges.TryGetValue(RepositorySources.ModuleOf(file)!, out var allowed) || !allowed.Contains(r.Module))
                .Select(r => $"{file.RelativePath} -> Domain.{r.Module}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Infrastructure_modules_do_not_reference_each_others_infrastructure()
    {
        var modules = ApplicationModules();
        var offenders = RepositorySources.Files("SporAkademi.Infrastructure")
            .Where(file => RepositorySources.ModuleOf(file) is { } m && modules.Contains(m))
            .Where(file => !IsCrossModuleReader(file.RelativePath))
            .SelectMany(file => RepositorySources.References(file, "Infrastructure")
                .Where(r => modules.Contains(r.Module) && r.Module != RepositorySources.ModuleOf(file) && !SharedKernel.Contains(r.Module))
                .Select(r => $"{file.RelativePath} -> Infrastructure.{r.Module}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Cross_module_reader_allowlist_has_no_stale_entries()
    {
        var files = RepositorySources.Files("SporAkademi.Infrastructure").Select(f => f.RelativePath).ToList();
        var stale = CrossModuleReaders.Concat(InfrastructureApplicationExceptions)
            .Where(entry => entry.EndsWith(".cs", StringComparison.Ordinal)
                ? !files.Contains(entry)
                : !files.Any(f => f.StartsWith($"{entry}/", StringComparison.Ordinal)))
            .ToList();

        stale.ShouldBeEmpty();
    }

    private static bool IsCrossModuleReader(string relativePath) =>
        CrossModuleReaders.Any(entry => entry.EndsWith(".cs", StringComparison.Ordinal)
            ? relativePath == entry
            : relativePath.StartsWith($"{entry}/", StringComparison.Ordinal));

    /// <summary>Folders of the Application project (Abstractions is the shared port layer, not a module).</summary>
    private static string[] ApplicationModules() =>
        [.. Assemblies.Application.GetTypes()
            .Select(t => t.Namespace?.Split('.'))
            .Where(parts => parts is { Length: > 2 } && parts[0] == "SporAkademi" && parts[1] == "Application" && parts[2] != "Abstractions")
            .Select(parts => parts![2])
            .Distinct()
            .Order()];

    private static List<(string From, string To)> DomainEdges()
    {
        var edges = RepositorySources.Files("SporAkademi.Domain")
            .Where(file => RepositorySources.ModuleOf(file) is { } m && !SharedKernel.Contains(m))
            .SelectMany(file => RepositorySources.References(file, "Domain")
                .Where(r => r.Module != RepositorySources.ModuleOf(file) && !SharedKernel.Contains(r.Module))
                .Select(r => (From: RepositorySources.ModuleOf(file)!, To: r.Module)))
            .Distinct()
            .ToList();

        // Domain modules do reference each other (IDs); none found means the scan is broken.
        return edges.Count > 0 ? edges : throw new InvalidOperationException("No Domain module edges found.");
    }

    private static string? DomainModuleOf(string ns)
    {
        const string prefix = "SporAkademi.Domain.";
        return ns.StartsWith(prefix, StringComparison.Ordinal) ? ns[prefix.Length..].Split('.')[0] : null;
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsArray)
        {
            return Unwrap(type.GetElementType()!);
        }

        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Unwrap).Append(type) : [type];
    }
}
