using System.Text.RegularExpressions;

namespace SporAkademi.ArchitectureTests;

/// <summary>Source-text helpers for the V1-02 rules (DEC-680): which module a file belongs to and which module namespaces it names.</summary>
internal static class RepositorySources
{
    public static string Root { get; } = FindRoot();

    /// <summary>Every <c>.cs</c> file of a <c>src/</c> project, without build output and generated EF migrations.</summary>
    public static IEnumerable<SourceFile> Files(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/Migrations/", StringComparison.Ordinal))
            .Select(path => new SourceFile(
                path[(Path.Combine(Root, "src", project).Replace('\\', '/').Length + 1)..],
                File.ReadAllText(path)));

    /// <summary>Lines of code without <c>//</c> comments (including <c>///</c> documentation).</summary>
    public static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

    /// <summary>Modules named by <c>SporAkademi.&lt;layer&gt;.&lt;Module&gt;</c> references in code, with whether the reference goes through <c>.Contracts</c>.</summary>
    public static IEnumerable<ModuleReference> References(SourceFile file, string layer) =>
        CodeLines(file.Text)
            .SelectMany(line => ReferenceRegex(layer).Matches(line).Select(m => m))
            .Select(m => new ModuleReference(m.Groups["module"].Value, m.Groups["contracts"].Success))
            .Distinct();

    /// <summary>First folder of the file inside its project, e.g. <c>Billing</c> for <c>Billing/Payment.cs</c>; <c>null</c> for root files.</summary>
    public static string? ModuleOf(SourceFile file)
    {
        var slash = file.RelativePath.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? null : file.RelativePath[..slash];
    }

    private static Regex ReferenceRegex(string layer) =>
        new($@"SporAkademi\.{layer}\.(?<module>[A-Za-z]+)(?<contracts>\.Contracts\b)?", RegexOptions.CultureInvariant);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SporAkademi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

internal sealed record SourceFile(string RelativePath, string Text);

internal sealed record ModuleReference(string Module, bool ThroughContracts);
