using System.Net;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Storage;
using SporAkademi.Infrastructure.Storage;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Storage;

/// <summary>
/// What every <see cref="IDocumentStorage"/> provider promises (DEC-323, DEC-650): private objects, reading only through a
/// short-lived link that fixes the served headers, idempotent delete. The S3 and the Azure Blob adapter run the same tests.
/// </summary>
public abstract class DocumentStorageContractTests
{
    private static readonly HttpClient Browser = new();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract IDocumentStorage Create(TimeSpan? linkLifetime = null);

    private static string NewKey() => $"{Guid.CreateVersion7()}/{Guid.CreateVersion7()}";

    private static byte[] Content() => [.. "%PDF-1.7 sentetik"u8];

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task An_uploaded_object_is_read_through_a_link_that_fixes_the_served_headers()
    {
        var storage = Create();
        var key = NewKey();
        await storage.PutAsync(key, Content(), "application/pdf", Ct);

        var link = storage.CreateDownloadLink(key, new DownloadDisposition("application/pdf", "saglik-raporu.pdf", Inline: false));

        using var response = await Browser.GetAsync(link.Url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Content());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldBe("saglik-raporu.pdf");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.CacheControl.Private.ShouldBeTrue();
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task The_link_decides_the_content_type_and_inline_display_not_the_stored_object()
    {
        var storage = Create();
        var key = NewKey();
        await storage.PutAsync(key, Content(), "text/html", Ct);

        var link = storage.CreateDownloadLink(key, new DownloadDisposition("image/jpeg", "fotograf.jpg", Inline: true));

        using var response = await Browser.GetAsync(link.Url, Ct);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task A_link_lives_for_the_configured_lifetime()
    {
        var storage = Create(TimeSpan.FromMinutes(7));
        var key = NewKey();
        await storage.PutAsync(key, Content(), "application/pdf", Ct);
        var before = DateTimeOffset.UtcNow;

        var link = storage.CreateDownloadLink(key, new DownloadDisposition("application/pdf", "saglik-raporu.pdf", Inline: false));

        link.ExpiresAt.ShouldBeInRange(before.AddMinutes(7).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(7).AddSeconds(5));
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task The_object_is_private_without_a_signature_or_with_a_changed_one()
    {
        var storage = Create();
        var key = NewKey();
        await storage.PutAsync(key, Content(), "application/pdf", Ct);
        var link = storage.CreateDownloadLink(key, new DownloadDisposition("application/pdf", "rapor.pdf", Inline: false));

        // Without a signature the store refuses (S3: 403, Azure: 404 for a private container); never the content.
        var unsigned = new Uri(link.Url.GetLeftPart(UriPartial.Path));
        (await Browser.GetAsync(unsigned, Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        // Another served disposition than the signed one is refused.
        var changed = new Uri(link.Url.AbsoluteUri.Replace("attachment", "inline", StringComparison.Ordinal));
        changed.ShouldNotBe(link.Url);
        (await Browser.GetAsync(changed, Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        // A link for one object does not open another.
        var otherKey = NewKey();
        await storage.PutAsync(otherKey, Content(), "application/pdf", Ct);
        var swapped = new Uri(link.Url.AbsoluteUri.Replace(key, otherKey, StringComparison.Ordinal));
        swapped.ShouldNotBe(link.Url);
        (await Browser.GetAsync(swapped, Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task A_link_stops_working_when_it_expires()
    {
        var storage = Create(TimeSpan.FromSeconds(1));
        var key = NewKey();
        await storage.PutAsync(key, Content(), "application/pdf", Ct);
        var link = storage.CreateDownloadLink(key, new DownloadDisposition("application/pdf", "rapor.pdf", Inline: false));

        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task Putting_the_same_key_again_replaces_the_object()
    {
        var storage = Create();
        var key = NewKey();
        await storage.PutAsync(key, Content(), "application/pdf", Ct);
        await storage.PutAsync(key, [1, 2, 3], "application/pdf", Ct);

        var link = storage.CreateDownloadLink(key, new DownloadDisposition("application/pdf", "rapor.pdf", Inline: false));

        (await Browser.GetByteArrayAsync(link.Url, Ct)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task A_deleted_object_is_gone_and_deleting_a_missing_one_succeeds()
    {
        var storage = Create();
        var key = NewKey();
        await storage.PutAsync(key, Content(), "application/pdf", Ct);
        var link = storage.CreateDownloadLink(key, new DownloadDisposition("application/pdf", "rapor.pdf", Inline: false));

        await storage.DeleteAsync(key, Ct);
        await storage.DeleteAsync(key, Ct);
        await storage.DeleteAsync(NewKey(), Ct);

        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task A_link_for_a_missing_object_is_signed_and_the_store_answers_not_found()
    {
        var storage = Create();

        var link = storage.CreateDownloadLink(NewKey(), new DownloadDisposition("application/pdf", "rapor.pdf", Inline: false));

        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public void Turkish_file_names_survive_in_the_content_disposition()
    {
        var header = DownloadHeaders.ContentDisposition(new DownloadDisposition("application/pdf", "sağlık raporu.pdf", Inline: false));

        header.ShouldStartWith("attachment; filename=\"sa_l_k raporu.pdf\"");
        header.ShouldContain("filename*=UTF-8''sa%C4%9Fl%C4%B1k%20raporu.pdf");
    }

    protected static IOptions<DocumentStorageOptions> OptionsOf(DocumentStorageOptions options) => Options.Create(options);
}

/// <summary>The S3-compatible adapter against the SeaweedFS container every integration test run already starts.</summary>
[Collection(DatabaseTestGroup.Name)]
public sealed class S3DocumentStorageContractTests(DatabaseFixture database) : DocumentStorageContractTests
{
    protected override IDocumentStorage Create(TimeSpan? linkLifetime = null)
    {
        var settings = database.Storage.Settings;
        var options = new DocumentStorageOptions
        {
            ServiceUrl = settings["Storage:ServiceUrl"],
            AccessKey = settings["Storage:AccessKey"],
            SecretKey = settings["Storage:SecretKey"],
            Bucket = settings["Storage:Bucket"],
            DownloadLinkLifetime = linkLifetime ?? TimeSpan.FromMinutes(5),
        };
        return new S3DocumentStorage(new S3Clients(OptionsOf(options)), new StorageClock(TimeProvider.System));
    }
}

/// <summary>The Azure Blob adapter against Azurite (shared-key signing is the test-only seam, see <see cref="AzuriteFixture"/>).</summary>
public sealed class AzureBlobDocumentStorageContractTests(AzuriteFixture azurite) : DocumentStorageContractTests, IClassFixture<AzuriteFixture>
{
    protected override IDocumentStorage Create(TimeSpan? linkLifetime = null) => azurite.CreateStorage(linkLifetime);
}
