using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Application.Abstractions.Storage;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Storage;
using SporAkademi.IntegrationTests.Enrollments;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.Testing;

namespace SporAkademi.IntegrationTests.Students;

/// <summary>
/// P2-F6: health reports and photos in S3-compatible storage (the compose.yaml SeaweedFS image). Uploads pass
/// through the API's content inspection (DEC-321, DEC-322), reads are short-lived signed links (DEC-323), and only
/// the School Admin and, for photos, the student's coach get them (DEC-324).
/// </summary>
[Trait("Story", "P2-F6")]
public sealed class DocumentTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    private static readonly HttpClient Browser = new();

    private string SchoolPrefix => $"{School.Value:N}/";

    [Fact]
    public async Task Admin_uploads_lists_downloads_and_deletes_a_health_report()
    {
        var student = await RegisterStudentAsync();
        var pdf = TestFiles.Pdf();

        var response = await UploadAsync(student, "HealthReport", pdf, "Sağlık Raporu 2027.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var document = await BodyOf(response);
        document.GetProperty("type").GetString().ShouldBe("HealthReport");
        document.GetProperty("contentType").GetString().ShouldBe("application/pdf");
        document.GetProperty("fileName").GetString().ShouldBe("Sağlık Raporu 2027.pdf");
        document.GetProperty("sizeBytes").GetInt64().ShouldBe(pdf.Length);
        document.GetProperty("uploadedAt").GetDateTimeOffset().ShouldBe(Api.Clock.GetUtcNow());
        document.TryGetProperty("storageKey", out _).ShouldBeFalse();

        // The key is generated: school/student/random, never the user's file name.
        var key = (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldHaveSingleItem();
        key.ShouldStartWith($"{School.Value:N}/{student:N}/");
        key.ShouldMatch("^[0-9a-f]{32}/[0-9a-f]{32}/[0-9a-f]{32}$");
        (await Database.Storage.ReadAsync(key)).ShouldBe(pdf);

        var list = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}/documents", Ct);
        list.EnumerateArray().Select(IdOf).ShouldBe([IdOf(document)]);

        var link = await LinkAsync(IdOf(document));
        using (var download = await Browser.GetAsync(link.Url, Ct))
        {
            download.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(pdf);
            download.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
            // A PDF is always a download, never rendered on the storage origin.
            download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            download.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("saglik-raporu.pdf");
        }

        (await Admin.DeleteAsync(new Uri($"/api/v1/documents/{IdOf(document)}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}/documents", Ct)).GetArrayLength().ShouldBe(0);
        // DEC-325: the object goes after commit, through the outbox (tests drive the processor themselves).
        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBe([key]);
        (await ProcessOutboxAsync()).ShouldBe(1);
        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBeEmpty();
        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var again = await Admin.GetAsync(new Uri($"/api/v1/documents/{IdOf(document)}/link", UriKind.Relative), Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(again)).ShouldBe("Document.NotFound");
        (await Admin.DeleteAsync(new Uri($"/api/v1/documents/{IdOf(document)}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_links_are_signed_short_lived_and_cannot_be_altered()
    {
        var student = await RegisterStudentAsync();
        var document = await BodyOf(await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "Ali Kaya rapor.pdf"));

        var before = DateTimeOffset.UtcNow;
        var link = await LinkAsync(IdOf(document));

        // DEC-323: five minutes, measured on the real clock the store checks signatures against.
        link.Url.Query.ShouldMatch("X-Amz-Expires=(29[5-9]|30[01])(&|$)");
        link.ExpiresAt.ShouldBeInRange(before.AddMinutes(5).AddSeconds(-5), DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
        // The user's file name (often a person's name) never appears in the link (it would reach storage access logs).
        link.Url.AbsoluteUri.ShouldNotContain("Kaya", Case.Insensitive);

        // The bucket is private: the object without a signature, or with any signed parameter changed, is refused.
        var unsigned = new Uri(link.Url.GetLeftPart(UriPartial.Path));
        (await Browser.GetAsync(unsigned, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var inline = new Uri(link.Url.AbsoluteUri.Replace("attachment", "inline", StringComparison.Ordinal));
        inline.ShouldNotBe(link.Url);
        (await Browser.GetAsync(inline, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // Change the key's last character to a different one (replacing it with a fixed "0" was a no-op 1 time in 16).
        var path = link.Url.AbsolutePath;
        var otherKey = new Uri(link.Url.AbsoluteUri.Replace(path, path[..^1] + (path[^1] == '0' ? '1' : '0'), StringComparison.Ordinal));
        otherKey.ShouldNotBe(link.Url);
        (await Browser.GetAsync(otherKey, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_links_stop_working_when_they_expire()
    {
        await using var shortLived = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DownloadLinkLifetime"] = "00:00:01" })));
        using var admin = shortLived.CreateClient();
        admin.DefaultRequestHeaders.Authorization = Admin.DefaultRequestHeaders.Authorization;
        var student = await RegisterStudentAsync();
        var document = await BodyOf(await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "rapor.pdf"));

        var link = (await admin.GetFromJsonAsync<LinkDto>($"/api/v1/documents/{IdOf(document)}/link", Ct))!;
        link.Url.Query.ShouldMatch("X-Amz-Expires=[12](&|$)");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        (await Browser.GetAsync(link.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Photos_lose_their_metadata_are_shown_inline_and_a_new_photo_replaces_the_old_one()
    {
        var student = await RegisterStudentAsync();

        var first = await BodyOf(await UploadAsync(student, "Photo", TestFiles.Jpeg(withTrailer: true), "IMG_2041.JPG"));
        first.GetProperty("fileName").GetString().ShouldBe("IMG_2041.jpg");
        var firstKey = (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldHaveSingleItem();
        var stored = await Database.Storage.ReadAsync(firstKey);
        TestFiles.Contains(stored, TestFiles.MetadataMarker).ShouldBeFalse();
        TestFiles.Contains(stored, TestFiles.TrailerMarker).ShouldBeFalse();
        first.GetProperty("sizeBytes").GetInt64().ShouldBe(stored.Length);

        var link = (await Admin.GetFromJsonAsync<LinkDto>($"/api/v1/students/{student}/photo", Ct))!;
        using (var image = await Browser.GetAsync(link.Url, Ct))
        {
            image.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
            image.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        }

        var second = await BodyOf(await UploadAsync(student, "Photo", TestFiles.WebP(), "yeni.webp"));

        var list = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}/documents", Ct);
        list.EnumerateArray().Select(IdOf).ShouldBe([IdOf(second)]);
        (await ProcessOutboxAsync()).ShouldBe(1);
        var key = (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldHaveSingleItem();
        key.ShouldNotBe(firstKey);
        (await Admin.GetFromJsonAsync<LinkDto>($"/api/v1/students/{student}/photo", Ct))!.Url.AbsolutePath.ShouldEndWith(key);
    }

    [Fact]
    public async Task A_failed_object_delete_is_retried_by_the_outbox_instead_of_being_orphaned()
    {
        var failing = new FlakyStorage();
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IDocumentStorage>(provider => failing.Wrap(ActivatorUtilities.CreateInstance<S3DocumentStorage>(provider)))));
        using var admin = api.CreateClient();
        admin.DefaultRequestHeaders.Authorization = Admin.DefaultRequestHeaders.Authorization;
        var student = await RegisterStudentAsync();
        var document = IdOf(await BodyOf(await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "rapor.pdf", client: admin)));
        var key = (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldHaveSingleItem();

        (await admin.DeleteAsync(new Uri($"/api/v1/documents/{document}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        failing.FailuresLeft = 1;
        await processor.ProcessBatchAsync(Ct);

        // The storage call failed: the object is still there and the message waits for its next attempt.
        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBe([key]);
        Api.Clock.Advance(TimeSpan.FromHours(1));
        (await processor.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBeEmpty();
        failing.Deletes.ShouldBe(2);
    }

    [Fact]
    public async Task A_student_without_a_photo_has_none_and_an_unknown_student_is_not_found()
    {
        var student = await RegisterStudentAsync();

        var none = await Admin.GetAsync(new Uri($"/api/v1/students/{student}/photo", UriKind.Relative), Ct);
        none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(none)).ShouldBe("Document.NotFound");

        var unknown = Guid.CreateVersion7();
        foreach (var url in new[] { $"/api/v1/students/{unknown}/photo", $"/api/v1/students/{unknown}/documents" })
        {
            var response = await Admin.GetAsync(new Uri(url, UriKind.Relative), Ct);
            (await CodeOf(response)).ShouldBe("Student.NotFound", url);
        }

        (await CodeOf(await UploadAsync(unknown, "Photo", TestFiles.Jpeg(), "a.jpg"))).ShouldBe("Student.NotFound");
        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBeEmpty();
    }

    public static TheoryData<string, string, string, string> MaliciousUploads() => new()
    {
        // type, file name, content kind, expected code
        { "Photo", "foto.png", "svg", "Document.UnsupportedType" },
        { "Photo", "foto.jpg", "html", "Document.UnsupportedType" },
        { "HealthReport", "rapor.pdf", "html", "Document.UnsupportedType" },
        { "HealthReport", "rapor.pdf", "exe", "Document.UnsupportedType" },
        { "Photo", "foto.gif", "gif", "Document.UnsupportedType" },
        { "Photo", "rapor.pdf", "pdf", "Document.UnsupportedType" },
        { "Photo", "foto.jpg.html", "jpeg", "Document.ExtensionMismatch" },
        { "HealthReport", "rapor.pdf.exe", "pdf", "Document.ExtensionMismatch" },
        { "HealthReport", "rapor.pdf", "pdf-js", "Document.ActiveContent" },
        { "HealthReport", "rapor.pdf", "pdf-html", "Document.UnsupportedType" },
        { "Photo", "buyuk.jpg", "jpeg-over-2mb", "Document.TooLarge" },
        { "Photo", "bos.jpg", "empty", "Document.Empty" },
    };

    [Theory]
    [MemberData(nameof(MaliciousUploads))]
    public async Task Hostile_files_are_refused_and_nothing_is_stored(string type, string fileName, string kind, string code)
    {
        var student = await RegisterStudentAsync();
        byte[] content = kind switch
        {
            "svg" => TestFiles.Svg(),
            "html" => TestFiles.Html(),
            "exe" => TestFiles.Executable(),
            "gif" => TestFiles.Gif(),
            "pdf" => TestFiles.Pdf(),
            "jpeg" => TestFiles.Jpeg(),
            "pdf-js" => TestFiles.PdfWithJavaScript(),
            "pdf-html" => TestFiles.PdfWithAppendedHtml(),
            "jpeg-over-2mb" => TestFiles.Jpeg(padTo: (2 * 1024 * 1024) + 1),
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // The declared content type is whatever the client claims; it is never trusted.
        var response = await UploadAsync(student, type, content, fileName, contentType: "image/jpeg");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe(code);
        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBeEmpty();
        (await CountRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Bodies_over_the_upload_limit_are_refused_before_they_are_read()
    {
        var student = await RegisterStudentAsync();

        var response = await UploadAsync(student, "HealthReport", new byte[(6 * 1024 * 1024) + 1], "rapor.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("Request.TooLarge");

        // The raised limit belongs to this endpoint only; JSON endpoints keep 1 MB.
        using var big = new StringContent(new string('a', 1_100_000), System.Text.Encoding.UTF8, "application/json");
        (await CodeOf(await Admin.PostAsync(new Uri("/api/v1/sports", UriKind.Relative), big, Ct))).ShouldBe("Request.TooLarge");
    }

    [Fact]
    public async Task Missing_or_unknown_form_fields_are_field_errors()
    {
        var student = await RegisterStudentAsync();

        var noType = await UploadAsync(student, null, TestFiles.Pdf(), "rapor.pdf");
        (await ErrorsOf(noType)).GetProperty("type")[0].GetString().ShouldBe("Field.Required");
        var badType = await UploadAsync(student, "Passport", TestFiles.Pdf(), "rapor.pdf");
        (await ErrorsOf(badType)).GetProperty("type")[0].GetString().ShouldBe("Field.Invalid");
        var numericType = await UploadAsync(student, "1", TestFiles.Jpeg(), "a.jpg");
        (await ErrorsOf(numericType)).GetProperty("type")[0].GetString().ShouldBe("Field.Invalid");
        var noFile = await UploadAsync(student, "Photo", null, "a.jpg");
        (await ErrorsOf(noFile)).GetProperty("file")[0].GetString().ShouldBe("Field.Required");
    }

    [Fact]
    public async Task A_student_holds_at_most_twenty_health_reports()
    {
        var student = await RegisterStudentAsync();
        for (var i = 0; i < 20; i++)
        {
            (await UploadAsync(student, "HealthReport", TestFiles.Pdf(), $"rapor-{i}.pdf")).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var response = await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "rapor-21.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await BodyOf(response);
        problem.GetProperty("code").GetString().ShouldBe("Document.LimitReached");
        problem.GetProperty("args").GetProperty("max").GetInt32().ShouldBe(20);
        (await Database.Storage.KeysAsync(SchoolPrefix)).Count.ShouldBe(20);
        // The photo is not counted.
        (await UploadAsync(student, "Photo", TestFiles.Png(), "foto.png")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_coach_sees_only_the_photos_of_students_in_own_active_groups_and_never_health_reports()
    {
        var group = IdOf(await GroupAsync("U10 Futbol", coach: Ayse));
        var mine = await RegisterStudentAsync("Can", "Deneme");
        var enrollment = IdOf(await EnrollOkAsync(mine, group, "2027-01-04"));
        var other = await RegisterStudentAsync("Ece", "Deneme");
        await UploadAsync(mine, "Photo", TestFiles.Png(), "can.png");
        await UploadAsync(other, "Photo", TestFiles.Png(), "ece.png");
        var report = IdOf(await BodyOf(await UploadAsync(mine, "HealthReport", TestFiles.Pdf(), "rapor.pdf")));
        using var ayse = CoachClient(Ayse);
        using var mehmet = CoachClient(Mehmet);

        var photo = await ayse.GetFromJsonAsync<LinkDto>($"/api/v1/students/{mine}/photo", Ct);
        (await Browser.GetAsync(photo!.Url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Another coach, or a student outside the coach's groups: exactly like an unknown student.
        foreach (var (client, student) in new[] { (mehmet, mine), (ayse, other) })
        {
            var response = await client.GetAsync(new Uri($"/api/v1/students/{student}/photo", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("Student.NotFound");
        }

        // DEC-324: health reports and the document list are School Admin only.
        (await ayse.GetAsync(new Uri($"/api/v1/documents/{report}/link", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ayse.GetAsync(new Uri($"/api/v1/students/{mine}/documents", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await UploadAsync(mine, "Photo", TestFiles.Png(), "x.png", client: ayse)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ayse.DeleteAsync(new Uri($"/api/v1/documents/{report}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Once the enrollment ends, the photo is out of reach.
        (await EndAsync(enrollment, Today)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ayse.GetAsync(new Uri($"/api/v1/students/{mine}/photo", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_school_cannot_reach_documents_and_never_sees_their_keys()
    {
        var student = await RegisterStudentAsync();
        var document = IdOf(await BodyOf(await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "rapor.pdf")));
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var other = Api.SchoolAdminClient(otherSchool);

        foreach (var (method, url, code) in new[]
        {
            ("GET", $"/api/v1/students/{student}/documents", "Student.NotFound"),
            ("GET", $"/api/v1/students/{student}/photo", "Student.NotFound"),
            ("GET", $"/api/v1/documents/{document}/link", "Document.NotFound"),
            ("DELETE", $"/api/v1/documents/{document}", "Document.NotFound"),
        })
        {
            var response = await other.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative)), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
            (await CodeOf(response)).ShouldBe(code, url);
        }

        (await CodeOf(await UploadAsync(student, "Photo", TestFiles.Png(), "a.png", client: other))).ShouldBe("Student.NotFound");
        (await Database.Storage.KeysAsync($"{otherSchool.Value:N}/")).ShouldBeEmpty();
        (await Database.Storage.KeysAsync(SchoolPrefix)).Count.ShouldBe(1);
        (await CountRowsAsync()).ShouldBe(1);
    }

    private async Task<HttpResponseMessage> UploadAsync(
        Guid studentId,
        string? type,
        byte[]? content,
        string fileName,
        HttpClient? client = null,
        string contentType = "application/octet-stream")
    {
        using var form = new MultipartFormDataContent();
        if (type is not null)
        {
            form.Add(new StringContent(type), "type");
        }

        if (content is not null)
        {
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", fileName);
        }

        return await (client ?? Admin).PostAsync(new Uri($"/api/v1/students/{studentId}/documents", UriKind.Relative), form, Ct);
    }

    private async Task<LinkDto> LinkAsync(Guid documentId) =>
        (await Admin.GetFromJsonAsync<LinkDto>($"/api/v1/documents/{documentId}/link", Ct))!;

    private static async Task<JsonElement> ErrorsOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");

    private async Task<long> CountRowsAsync()
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM students.student_documents", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task When_the_database_part_fails_after_the_upload_the_new_object_is_deleted()
    {
        var student = await RegisterStudentAsync();
        var existing = (await Database.Storage.KeysAsync(SchoolPrefix)).Count;

        // An unexpected failure (exception) in the database part: 500, and the stored object is removed again.
        await using (var throwing = WithDocuments(inner => new FaultyDocuments(inner) { ThrowOnAdd = true }))
        {
            using var admin = AuthorizedClient(throwing);
            var response = await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "rapor.pdf", client: admin);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }

        (await Database.Storage.KeysAsync(SchoolPrefix)).Count.ShouldBe(existing);
        (await CountRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task The_report_limit_is_checked_again_under_the_lock_and_a_late_refusal_removes_the_object()
    {
        var student = await RegisterStudentAsync();
        for (var i = 0; i < 20; i++)
        {
            (await UploadAsync(student, "HealthReport", TestFiles.Pdf(), $"r{i}.pdf")).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // The fast pre-check is fooled (as by a concurrent upload); the re-count inside the transaction still refuses.
        await using var racing = WithDocuments(inner => new FaultyDocuments(inner) { FirstCountsAsZero = true });
        using var admin = AuthorizedClient(racing);
        var response = await UploadAsync(student, "HealthReport", TestFiles.Pdf(), "r21.pdf", client: admin);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Document.LimitReached");
        (await Database.Storage.KeysAsync(SchoolPrefix)).Count.ShouldBe(20);
        (await CountRowsAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task Concurrent_photo_uploads_never_fail_with_500_and_leave_one_photo()
    {
        var student = await RegisterStudentAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => UploadAsync(student, "Photo", TestFiles.Png(), $"foto{i}.png")));

        responses.Select(r => (int)r.StatusCode).ShouldAllBe(status => status == 201 || status == 409);
        responses.ShouldContain(r => r.StatusCode == HttpStatusCode.Created);
        while (await ProcessOutboxAsync() > 0)
        {
        }

        (await CountRowsAsync()).ShouldBe(1);
        var key = (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldHaveSingleItem();
        (await Admin.GetFromJsonAsync<LinkDto>($"/api/v1/students/{student}/photo", Ct))!.Url.AbsolutePath.ShouldEndWith(key);
    }

    [Fact]
    public async Task Deleting_an_object_is_idempotent_so_a_repeated_delivery_is_harmless()
    {
        var student = await RegisterStudentAsync();
        await UploadAsync(student, "Photo", TestFiles.Png(), "foto.png");
        var key = (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldHaveSingleItem();
        var message = new Application.Students.Contracts.DocumentObjectDeletedMessage(key);
        var context = new Application.Abstractions.Outbox.OutboxMessageContext(Guid.CreateVersion7(), School.Value, Api.Clock.GetUtcNow(), 1);

        await InScopeAsync(async services =>
        {
            var handler = services.GetServices<Application.Abstractions.Outbox.IOutboxMessageHandler<Application.Students.Contracts.DocumentObjectDeletedMessage>>().ShouldHaveSingleItem();
            await handler.Handle(message, context, Ct);
            await handler.Handle(message, context with { Attempt = 2 }, Ct);
        });

        (await Database.Storage.KeysAsync(SchoolPrefix)).ShouldBeEmpty();
    }

    private WebApplicationFactory<Program> WithDocuments(Func<Application.Students.Documents.IStudentDocumentRepository, Application.Students.Documents.IStudentDocumentRepository> wrap) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(provider => wrap(ActivatorUtilities.CreateInstance<SporAkademi.Infrastructure.Students.StudentDocumentRepository>(provider)))));

    private HttpClient AuthorizedClient(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = Admin.DefaultRequestHeaders.Authorization;
        return client;
    }

    private Task<int> ProcessOutboxAsync() => Api.Services.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(Ct);

    /// <summary>The real repository with injectable faults.</summary>
    private sealed class FaultyDocuments(Application.Students.Documents.IStudentDocumentRepository inner) : Application.Students.Documents.IStudentDocumentRepository
    {
        private bool _counted;

        public bool ThrowOnAdd { get; init; }

        public bool FirstCountsAsZero { get; init; }

        public Task LockStudentAsync(Domain.Students.StudentId studentId, CancellationToken cancellationToken) => inner.LockStudentAsync(studentId, cancellationToken);

        public void Add(Domain.Students.StudentDocument document)
        {
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("Simulated database failure.");
            }

            inner.Add(document);
        }

        public void Remove(Domain.Students.StudentDocument document) => inner.Remove(document);

        public Task<Domain.Students.StudentDocument?> FindAsync(Domain.Students.StudentDocumentId id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

        public Task<Domain.Students.StudentDocument?> FindPhotoAsync(Domain.Students.StudentId studentId, CancellationToken cancellationToken) => inner.FindPhotoAsync(studentId, cancellationToken);

        public Task<Domain.Students.StudentDocument?> FindForReadingAsync(Domain.Students.StudentDocumentId id, CancellationToken cancellationToken) => inner.FindForReadingAsync(id, cancellationToken);

        public Task<Domain.Students.StudentDocument?> FindPhotoForReadingAsync(Domain.Students.StudentId studentId, CancellationToken cancellationToken) => inner.FindPhotoForReadingAsync(studentId, cancellationToken);

        public Task<int> CountAsync(Domain.Students.StudentId studentId, Domain.Students.DocumentType type, CancellationToken cancellationToken)
        {
            if (FirstCountsAsZero && !_counted)
            {
                _counted = true;
                return Task.FromResult(0);
            }

            return inner.CountAsync(studentId, type, cancellationToken);
        }

        public Task<IReadOnlyList<Domain.Students.StudentDocument>> ListOfStudentAsync(Domain.Students.StudentId studentId, CancellationToken cancellationToken) =>
            inner.ListOfStudentAsync(studentId, cancellationToken);
    }

    private sealed record LinkDto(Uri Url, DateTimeOffset ExpiresAt);

    /// <summary>The real store, with object deletes that fail a given number of times.</summary>
    private sealed class FlakyStorage
    {
        public int FailuresLeft { get; set; }

        public int Deletes { get; private set; }

        public IDocumentStorage Wrap(IDocumentStorage inner) => new Wrapper(this, inner);

        private sealed class Wrapper(FlakyStorage owner, IDocumentStorage inner) : IDocumentStorage
        {
            public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
                inner.PutAsync(key, content, contentType, cancellationToken);

            public Task DeleteAsync(string key, CancellationToken cancellationToken)
            {
                owner.Deletes++;
                if (owner.FailuresLeft > 0)
                {
                    owner.FailuresLeft--;
                    throw new HttpRequestException("Storage unavailable.");
                }

                return inner.DeleteAsync(key, cancellationToken);
            }

            public DownloadLink CreateDownloadLink(string key, DownloadDisposition disposition) => inner.CreateDownloadLink(key, disposition);
        }
    }
}
