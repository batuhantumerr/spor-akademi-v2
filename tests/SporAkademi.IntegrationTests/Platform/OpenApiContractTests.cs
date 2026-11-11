using System.Text.Json;
using System.Text.Json.Nodes;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Platform;

/// <summary>
/// The Angular client (P1-14) and the parent app's client (P3-04) are generated from committed copies of the
/// OpenAPI document, one per app so each app builds on its own. This test fails when the API contract changed
/// but a committed copy did not. To update both, run the tests with UPDATE_OPENAPI=1 and commit the files.
/// </summary>
public sealed class OpenApiContractTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static TheoryData<string> CommittedDocuments => new()
    {
        "web/admin/openapi/spor-akademi.json",
        "web/parent/openapi/spor-akademi.json",
    };

    [Theory]
    [MemberData(nameof(CommittedDocuments))]
    public async Task Committed_openapi_document_matches_the_api(string relativePath)
    {
        var json = await Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var actual = JsonNode.Parse(json)!.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine([RepositoryRoot(), .. relativePath.Split('/')]);

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            // Regenerating in CI would compare the file with itself and hide drift.
            Environment.GetEnvironmentVariable("CI").ShouldBeNullOrEmpty("UPDATE_OPENAPI must not be used in CI.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual, TestContext.Current.CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"Missing {path}; run the tests with UPDATE_OPENAPI=1.");
        var committed = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        committed.ShouldBe(actual, $"The API contract changed; run the tests with UPDATE_OPENAPI=1 and commit {relativePath}.");
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
