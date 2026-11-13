using System.Net;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Audit;

/// <summary>
/// P4-06 (DEC-548): the document store is optional at start (DEC-326; e.g. the e2e API runs without one). A student
/// without documents is exported without touching it; only links to existing documents need the store.
/// </summary>
[Trait("Story", "P4-06")]
public sealed class DataExportWithoutStorageTests(DatabaseFixture database) : PersonalDataTest(database)
{
    protected override ApiFactory CreateApi() => new(Database.AppConnectionString);

    [Fact]
    public async Task A_student_without_documents_is_exported_while_the_document_store_is_not_configured()
    {
        var response = await Admin.GetAsync(new Uri($"/api/v1/students/{Ali}/data-export", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var export = await BodyOf(response);
        export.GetProperty("student").GetProperty("id").GetGuid().ShouldBe(Ali);
        export.GetProperty("documents").GetArrayLength().ShouldBe(0);
        (await AuditRowsAsync()).ShouldContain(r => r.Action == "Export" && r.EntityId == Ali);
    }
}
