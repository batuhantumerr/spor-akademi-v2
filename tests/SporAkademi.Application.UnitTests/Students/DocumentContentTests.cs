using SporAkademi.Application.Students.Documents;
using SporAkademi.Domain.Students;
using SporAkademi.Testing;

namespace SporAkademi.Application.UnitTests.Students;

/// <summary>P2-F6, DEC-322: what an upload really is is decided by its bytes, never by its name or declared type.</summary>
public sealed class DocumentContentTests
{
    [Theory]
    [InlineData(DocumentType.HealthReport, "rapor.pdf", DocumentFormat.Pdf)]
    [InlineData(DocumentType.HealthReport, "tarama.jpg", DocumentFormat.Jpeg)]
    [InlineData(DocumentType.HealthReport, "tarama.png", DocumentFormat.Png)]
    [InlineData(DocumentType.Photo, "foto.JPEG", DocumentFormat.Jpeg)]
    [InlineData(DocumentType.Photo, "foto.png", DocumentFormat.Png)]
    [InlineData(DocumentType.Photo, "foto.webp", DocumentFormat.WebP)]
    public void Accepts_the_formats_of_each_type(DocumentType type, string name, DocumentFormat format)
    {
        var result = DocumentContent.Inspect(type, name, FileOf(format));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);
        result.Value.Format.ShouldBe(format);
    }

    [Theory]
    [InlineData(DocumentType.Photo, DocumentFormat.Pdf)]
    [InlineData(DocumentType.HealthReport, DocumentFormat.WebP)]
    public void Refuses_a_format_the_type_does_not_accept(DocumentType type, DocumentFormat format)
    {
        DocumentContent.Inspect(type, null, FileOf(format)).Error.Code.ShouldBe("Document.UnsupportedType");
    }

    public static TheoryData<string, byte[]> HostileFiles() => new()
    {
        { "foto.png", TestFiles.Svg() },
        { "foto.jpg", TestFiles.Html() },
        { "rapor.pdf", TestFiles.Html() },
        { "rapor.pdf", TestFiles.Executable() },
        { "foto.gif", TestFiles.Gif() },
        { "foto.jpg", "not an image"u8.ToArray() },
        // Magic bytes alone are not enough: the structure must parse.
        { "foto.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00] },
        { "foto.png", [.. TestFiles.Png().Take(30)] },
        { "rapor.pdf", "%PDF-1.7\n<html>no end marker</html>"u8.ToArray() },
    };

    [Theory]
    [MemberData(nameof(HostileFiles))]
    public void Refuses_content_that_is_not_what_it_claims(string name, byte[] content)
    {
        var type = name.EndsWith(".pdf", StringComparison.Ordinal) ? DocumentType.HealthReport : DocumentType.Photo;

        DocumentContent.Inspect(type, name, content).Error.Code.ShouldBe("Document.UnsupportedType");
    }

    [Fact]
    public void Ignores_the_name_when_deciding_the_format()
    {
        // A real PNG named .jpg is still a PNG; the name only has to fit the detected format.
        DocumentContent.Inspect(DocumentType.Photo, "foto.jpg", TestFiles.Png()).Error.Code.ShouldBe("Document.ExtensionMismatch");
        DocumentContent.Inspect(DocumentType.Photo, "foto", TestFiles.Png()).Value.FileName.ShouldBe("foto.png");
    }

    [Theory]
    [InlineData("foto.jpg.html")]
    [InlineData("foto.jpg.exe")]
    [InlineData("foto.jpg.svg")]
    [InlineData("rapor.pdf.js")]
    public void Refuses_double_extensions_that_end_in_another_type(string name)
    {
        var type = name.StartsWith("rapor", StringComparison.Ordinal) ? DocumentType.HealthReport : DocumentType.Photo;
        var content = type == DocumentType.HealthReport ? TestFiles.Pdf() : TestFiles.Jpeg();

        DocumentContent.Inspect(type, name, content).Error.Code.ShouldBe("Document.ExtensionMismatch");
    }

    [Fact]
    public void Enforces_the_size_limit_of_each_type()
    {
        var photoLimit = (int)DocumentRules.PhotoMaxBytes;

        DocumentContent.Inspect(DocumentType.Photo, "a.jpg", TestFiles.Jpeg(padTo: photoLimit)).IsSuccess.ShouldBeTrue();
        DocumentContent.Inspect(DocumentType.Photo, "a.jpg", TestFiles.Jpeg(padTo: photoLimit + 1)).Error.Code.ShouldBe("Document.TooLarge");
        // The same file is fine as a health report, whose limit is 5 MB.
        DocumentContent.Inspect(DocumentType.HealthReport, "a.jpg", TestFiles.Jpeg(padTo: photoLimit + 1)).IsSuccess.ShouldBeTrue();
        DocumentContent.Inspect(DocumentType.HealthReport, "a.jpg", TestFiles.Jpeg(padTo: (int)DocumentRules.HealthReportMaxBytes + 1)).Error.Code.ShouldBe("Document.TooLarge");
        DocumentContent.Inspect(DocumentType.Photo, "a.jpg", []).Error.Code.ShouldBe("Document.Empty");
    }

    [Theory]
    [InlineData(DocumentFormat.Jpeg)]
    [InlineData(DocumentFormat.Png)]
    [InlineData(DocumentFormat.WebP)]
    public void Strips_image_metadata(DocumentFormat format)
    {
        var original = FileOf(format);
        TestFiles.Contains(original, TestFiles.MetadataMarker).ShouldBeTrue();

        var cleaned = DocumentContent.Inspect(DocumentType.Photo, null, original).Value.Content;

        TestFiles.Contains(cleaned, TestFiles.MetadataMarker).ShouldBeFalse();
        DocumentContent.Detect(cleaned).ShouldBe(format);
        // Stripping is stable: a cleaned file passes again unchanged.
        DocumentContent.Inspect(DocumentType.Photo, null, cleaned).Value.Content.ShouldBe(cleaned);
    }

    [Fact]
    public void Keeps_the_image_data_and_the_jfif_header()
    {
        var cleaned = DocumentContent.Inspect(DocumentType.Photo, null, TestFiles.Jpeg()).Value.Content;

        TestFiles.Contains(cleaned, "JFIF").ShouldBeTrue();
        cleaned[..2].ShouldBe(new byte[] { 0xFF, 0xD8 });
        cleaned[^2..].ShouldBe(new byte[] { 0xFF, 0xD9 });
    }

    [Fact]
    public void Clears_the_metadata_flags_of_an_extended_webp()
    {
        var cleaned = DocumentContent.Inspect(DocumentType.Photo, null, TestFiles.WebP()).Value.Content;

        (cleaned[20] & 0x0C).ShouldBe(0);
        BitConverter.ToUInt32(cleaned, 4).ShouldBe((uint)(cleaned.Length - 8));
    }

    [Theory]
    [InlineData(DocumentFormat.Jpeg)]
    [InlineData(DocumentFormat.Png)]
    public void Drops_anything_appended_after_the_image(DocumentFormat format)
    {
        var polyglot = format == DocumentFormat.Jpeg ? TestFiles.Jpeg(withTrailer: true) : TestFiles.Png(withTrailer: true);

        var cleaned = DocumentContent.Inspect(DocumentType.Photo, null, polyglot).Value.Content;

        TestFiles.Contains(cleaned, TestFiles.TrailerMarker).ShouldBeFalse();
    }

    [Fact]
    public void Refuses_pdfs_with_active_content_even_when_the_name_is_escaped()
    {
        DocumentContent.Inspect(DocumentType.HealthReport, "r.pdf", TestFiles.PdfWithJavaScript()).Error.Code.ShouldBe("Document.ActiveContent");
        DocumentContent.Inspect(DocumentType.HealthReport, "r.pdf", TestFiles.PdfWithEscapedJavaScript()).Error.Code.ShouldBe("Document.ActiveContent");
        DocumentContent.Inspect(DocumentType.HealthReport, "r.pdf", TestFiles.PdfWithLaunch()).Error.Code.ShouldBe("Document.ActiveContent");
    }

    [Fact]
    public void Refuses_a_pdf_with_content_after_its_end_marker()
    {
        DocumentContent.Inspect(DocumentType.HealthReport, "r.pdf", TestFiles.PdfWithAppendedHtml()).Error.Code.ShouldBe("Document.UnsupportedType");
    }

    [Fact]
    public void A_plain_pdf_is_stored_byte_for_byte()
    {
        var pdf = TestFiles.Pdf();

        DocumentContent.Inspect(DocumentType.HealthReport, "r.pdf", pdf).Value.Content.ShouldBe(pdf);
    }

    [Theory]
    [InlineData("C:\\Users\\veli\\Desktop\\Sağlık Raporu.pdf", "Sağlık Raporu.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("<img src=x onerror=alert(1)>.pdf", "img src_x onerror_alert(1).pdf")]
    [InlineData("rapor\"; filename=evil.html.pdf", "rapor__ filename_evil.html.pdf")]
    [InlineData("rapor\u0000\u202Efdp.exe.pdf", "rapor__fdp.exe.pdf")]
    [InlineData("   ...pdf", "saglik-raporu.pdf")]
    [InlineData(null, "saglik-raporu.pdf")]
    [InlineData("rapor.PDF", "rapor.pdf")]
    public void Keeps_only_a_safe_display_name(string? name, string expected)
    {
        DocumentContent.SafeFileName(name, DocumentType.HealthReport, DocumentFormat.Pdf).ShouldBe(expected);
    }

    [Fact]
    public void Shortens_long_names_and_keeps_the_extension()
    {
        var name = DocumentContent.SafeFileName(new string('ş', 300) + ".jpg", DocumentType.Photo, DocumentFormat.Jpeg);

        name.Length.ShouldBeLessThanOrEqualTo(StudentDocument.FileNameMaxLength);
        name.ShouldEndWith(".jpg");
        DocumentContent.SafeFileName(null, DocumentType.Photo, DocumentFormat.WebP).ShouldBe("fotograf.webp");
    }

    private static byte[] FileOf(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => TestFiles.Pdf(),
        DocumentFormat.Jpeg => TestFiles.Jpeg(),
        DocumentFormat.Png => TestFiles.Png(),
        _ => TestFiles.WebP(),
    };
}
