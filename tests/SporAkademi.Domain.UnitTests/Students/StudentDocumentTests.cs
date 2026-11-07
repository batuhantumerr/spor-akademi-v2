using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Students;

/// <summary>P2-F6, DEC-322: formats and size limits per document type.</summary>
public sealed class StudentDocumentTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(DocumentType.HealthReport, DocumentFormat.Pdf, true)]
    [InlineData(DocumentType.HealthReport, DocumentFormat.Jpeg, true)]
    [InlineData(DocumentType.HealthReport, DocumentFormat.Png, true)]
    [InlineData(DocumentType.HealthReport, DocumentFormat.WebP, false)]
    [InlineData(DocumentType.Photo, DocumentFormat.Jpeg, true)]
    [InlineData(DocumentType.Photo, DocumentFormat.Png, true)]
    [InlineData(DocumentType.Photo, DocumentFormat.WebP, true)]
    [InlineData(DocumentType.Photo, DocumentFormat.Pdf, false)]
    public void Each_type_accepts_its_formats(DocumentType type, DocumentFormat format, bool accepted)
    {
        DocumentRules.Accepts(type, format).ShouldBe(accepted);
        Create(type, format, 100).IsSuccess.ShouldBe(accepted);
    }

    [Fact]
    public void Size_limits_are_5_MB_for_reports_and_2_MB_for_photos()
    {
        Create(DocumentType.HealthReport, DocumentFormat.Pdf, 5 * 1024 * 1024).IsSuccess.ShouldBeTrue();
        Create(DocumentType.HealthReport, DocumentFormat.Pdf, (5 * 1024 * 1024) + 1).Error.ShouldBe(DocumentErrors.TooLarge);
        Create(DocumentType.Photo, DocumentFormat.Jpeg, 2 * 1024 * 1024).IsSuccess.ShouldBeTrue();
        Create(DocumentType.Photo, DocumentFormat.Jpeg, (2 * 1024 * 1024) + 1).Error.ShouldBe(DocumentErrors.TooLarge);
        Create(DocumentType.Photo, DocumentFormat.Jpeg, 0).Error.ShouldBe(DocumentErrors.Empty);
    }

    [Fact]
    public void Content_types_and_extensions_come_from_the_detected_format()
    {
        DocumentRules.ContentType(DocumentFormat.Pdf).ShouldBe("application/pdf");
        DocumentRules.ContentType(DocumentFormat.WebP).ShouldBe("image/webp");
        DocumentRules.Extension(DocumentFormat.Jpeg).ShouldBe(".jpg");
        DocumentRules.Extensions(DocumentFormat.Jpeg).ShouldBe([".jpg", ".jpeg"]);
    }

    private static Result<StudentDocument> Create(DocumentType type, DocumentFormat format, long size) =>
        StudentDocument.Create(
            SchoolId.From(Guid.CreateVersion7()),
            StudentId.From(Guid.CreateVersion7()),
            type,
            format,
            "dosya" + DocumentRules.Extension(format),
            size,
            "school/student/key",
            UserId.From(Guid.CreateVersion7()),
            Now);
}
