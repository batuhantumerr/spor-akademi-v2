using System.Text.Json;
using System.Text.Json.Nodes;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Platform;

/// <summary>
/// The Angular client is generated from web/admin/openapi/spor-akademi.json (P1-14). This test fails
/// when the API contract changed but the committed document did not. To update it, run the tests
/// with UPDATE_OPENAPI=1 and commit the file.
/// </summary>
public sealed class OpenApiContractTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task Committed_openapi_document_matches_the_api()
    {
        var json = await Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var actual = JsonNode.Parse(json)!.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(RepositoryRoot(), "web", "admin", "openapi", "spor-akademi.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            // Regenerating in CI would compare the file with itself and hide drift.
            Environment.GetEnvironmentVariable("CI").ShouldBeNullOrEmpty("UPDATE_OPENAPI must not be used in CI.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual, TestContext.Current.CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"Missing {path}; run the tests with UPDATE_OPENAPI=1.");
        var committed = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        committed.ShouldBe(actual, "The API contract changed; run the tests with UPDATE_OPENAPI=1 and commit web/admin/openapi/spor-akademi.json.");
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
