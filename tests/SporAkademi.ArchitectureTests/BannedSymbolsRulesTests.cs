namespace SporAkademi.ArchitectureTests;

/// <summary>V1-02 (DEC-683): symbols and packages the project decided against (CLAUDE.md, ADR-0001, ADR-0005, DEC-049) stay out.</summary>
[Trait("Story", "V1-02")]
public sealed class BannedSymbolsRulesTests
{
    private static readonly string[] BannedPackages = ["MediatR", "AutoMapper", "FluentAssertions"];

    /// <summary>
    /// Files that may take <c>FOR UPDATE</c>/<c>FOR SHARE</c> locks or write the keyword in SQL (reason in brackets). Enrollment
    /// locks (student, group, enrollment rows) belong to <c>IEnrollmentLocks</c> in <c>Enrollments/EnrollmentsPersistence.cs</c>.
    /// </summary>
    private static readonly string[] RowLockFiles =
    [
        "SporAkademi.Infrastructure/Enrollments/EnrollmentsPersistence.cs", // IEnrollmentLocks (ADR-0005, DEC-049)
        "SporAkademi.Infrastructure/Outbox/OutboxProcessor.cs", // FOR UPDATE SKIP LOCKED batch claim (DEC-302)
        "SporAkademi.Infrastructure/Persistence/RowLevelSecurity.cs", // CREATE POLICY ... FOR UPDATE, not a row lock
        "SporAkademi.Infrastructure/Students/StudentsPersistence.cs", // guardian row lock before delete/link (DEC-368)
    ];

    [Fact]
    public void Guid_new_guid_is_never_used_ids_are_version_7()
    {
        var offenders = AllCode()
            .Where(f => f.Lines.Any(l => l.Contains("Guid.NewGuid", StringComparison.Ordinal)))
            .Select(f => f.Path)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Banned_packages_are_referenced_by_no_project()
    {
        var files = Directory.EnumerateFiles(RepositorySources.Root, "*", SearchOption.AllDirectories)
            .Select(p => "/" + Path.GetRelativePath(RepositorySources.Root, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal)
                && !p.Contains("/node_modules/", StringComparison.Ordinal) && !p.Contains("/.git/", StringComparison.Ordinal)
                && !p.Contains("/.claude/", StringComparison.Ordinal))
            .Where(p => p.EndsWith(".csproj", StringComparison.Ordinal) || p.EndsWith(".props", StringComparison.Ordinal)
                || p.EndsWith(".targets", StringComparison.Ordinal) || p.EndsWith("packages.lock.json", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        var offenders = files
            .Where(p => BannedPackages.Any(b => File.ReadAllText(Path.Combine(RepositorySources.Root, p[1..])).Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p[1..])
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Row_locks_are_only_taken_in_the_named_files()
    {
        var offenders = AllCode()
            .Where(f => f.Lines.Any(l => l.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                || l.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase)
                || l.Contains("FOR NO KEY UPDATE", StringComparison.OrdinalIgnoreCase)))
            .Where(f => !RowLockFiles.Contains(f.Path))
            .Select(f => f.Path)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Enrollment_row_locks_are_only_taken_through_enrollment_locks()
    {
        // Student, group and enrollment rows are locked student first, then group, only by IEnrollmentLocks (CLAUDE.md).
        // Checked per file, so SQL split over several lines is caught too.
        string[] lockedTables = ["students.students", "groups.groups", "enrollments.enrollments"];
        var offenders = AllCode()
            .Where(f => f.Path != "SporAkademi.Infrastructure/Enrollments/EnrollmentsPersistence.cs")
            .Select(f => (f.Path, Code: string.Join("\n", f.Lines)))
            .Where(f => (f.Code.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase) || f.Code.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase))
                && lockedTables.Any(t => f.Code.Contains(t, StringComparison.Ordinal)))
            .Select(f => f.Path)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Enrollment_locks_implementation_is_the_only_row_lock_in_the_enrollments_file()
    {
        // The named file must keep implementing IEnrollmentLocks, otherwise the allowlist entry above is stale.
        var file = RepositorySources.Files("SporAkademi.Infrastructure")
            .Single(f => f.RelativePath == "Enrollments/EnrollmentsPersistence.cs");

        file.Text.ShouldContain("IEnrollmentLocks");
    }

    /// <summary>Code lines (comments removed) of every source file, with the path as <c>Project/relative/path.cs</c>.</summary>
    private static IEnumerable<(string Path, string[] Lines)> AllCode() =>
        Directory.EnumerateDirectories(Path.Combine(RepositorySources.Root, "src"))
            .Select(Path.GetFileName)
            .SelectMany(project => RepositorySources.Files(project!)
                .Select(file => (Path: $"{project}/{file.RelativePath}", Lines: RepositorySources.CodeLines(file.Text).ToArray())));
}
