using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hangfire.PostgreSql;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// The Hangfire storage schema is created by the migrator role, never by the API (DEC-331): a migration runs the
/// install scripts that ship inside Hangfire.PostgreSql, exactly as its own installer would (each script, then the
/// version bump), and the API starts storage with <c>PrepareSchemaIfNecessary = false</c>. The app role only gets
/// CRUD through the database's default privileges.
/// </summary>
internal static partial class HangfireSchema
{
    public const string Name = "hangfire";

    /// <summary>
    /// The script version the migrations install. Upgrading Hangfire.PostgreSql to a package with newer scripts
    /// needs a migration that installs the new versions and raises this constant; a test compares the two.
    /// </summary>
    public const int InstalledVersion = 23;

    private const string ResourcePrefix = "Hangfire.PostgreSql.Scripts.Install.v";

    /// <summary>The highest install script version in the referenced Hangfire.PostgreSql package.</summary>
    public static int PackagedVersion() =>
        typeof(PostgreSqlStorage).Assembly.GetManifestResourceNames()
            .Select(name => ScriptVersion().Match(name))
            .Where(match => match.Success)
            .Max(match => int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture));

    /// <summary>
    /// The SQL of install scripts <paramref name="fromVersion"/> to <paramref name="toVersion"/> with the version
    /// bump the installer adds after each. Script 3 is the first; on a fresh schema no script's "already applied"
    /// guard fires, and on an upgrade the guard of every new version passes.
    /// </summary>
    public static string InstallScript(int fromVersion, int toVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(toVersion, fromVersion);

        var sql = new StringBuilder();
        for (var version = fromVersion; version <= toVersion; version++)
        {
            var previous = version == 3 ? 1 : version - 1;
            sql.AppendLine(Read(version).TrimStart('﻿').TrimEnd().TrimEnd(';'))
                .AppendLine(";")
                .Append(CultureInfo.InvariantCulture, $"UPDATE \"{Name}\".\"schema\" SET \"version\" = {version} WHERE \"version\" = {previous};")
                .AppendLine();
        }

        return sql.ToString();
    }

    private static string Read(int version)
    {
        var resource = $"{ResourcePrefix}{version.ToString(CultureInfo.InvariantCulture)}.sql";
        using var stream = typeof(PostgreSqlStorage).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Hangfire.PostgreSql has no install script {resource}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^Hangfire\.PostgreSql\.Scripts\.Install\.v(?<version>\d+)\.sql$")]
    private static partial Regex ScriptVersion();
}
