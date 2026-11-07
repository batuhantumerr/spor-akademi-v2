using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Students;

public readonly record struct StudentDocumentId(Guid Value) : IEntityId<StudentDocumentId>
{
    public static StudentDocumentId From(Guid value) => new(value);
}

/// <summary>What a stored document is (P2-F6). The type decides the accepted formats and the size limit (DEC-322).</summary>
public enum DocumentType
{
    /// <summary>Sağlık raporu: special category personal data under KVKK; School Admin only (DEC-324).</summary>
    HealthReport,

    /// <summary>The student's photo; at most one per student, a new upload replaces it.</summary>
    Photo,
}

/// <summary>A file format recognised by its content (magic bytes), never by the name or the declared content type.</summary>
public enum DocumentFormat
{
    Pdf,
    Jpeg,
    Png,
    WebP,
}

/// <summary>
/// The metadata of a file of a student kept in object storage (P2-F6). The bytes live under
/// <see cref="StorageKey"/>, a generated key that never contains user input; the original name is kept
/// only here, sanitised, for display and the download name.
/// </summary>
public sealed class StudentDocument : AggregateRoot<StudentDocumentId>, ITenantScoped
{
    public const int FileNameMaxLength = 100;
    public const int StorageKeyMaxLength = 200;

    private StudentDocument(
        StudentDocumentId id,
        SchoolId tenantId,
        StudentId studentId,
        DocumentType type,
        DocumentFormat format,
        string fileName,
        long sizeBytes,
        string storageKey,
        UserId uploadedBy,
        DateTimeOffset uploadedAt)
        : base(id)
    {
        TenantId = tenantId;
        StudentId = studentId;
        Type = type;
        Format = format;
        FileName = fileName;
        SizeBytes = sizeBytes;
        StorageKey = storageKey;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
    }

    // For EF Core materialization only.
    private StudentDocument()
        : base(default)
    {
        FileName = null!;
        StorageKey = null!;
    }

    public SchoolId TenantId { get; private set; }

    public StudentId StudentId { get; private set; }

    public DocumentType Type { get; private set; }

    public DocumentFormat Format { get; private set; }

    /// <summary>Sanitised original name with the extension of the detected format; display only.</summary>
    public string FileName { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>Object key: <c>{school}/{student}/{random}</c>. Never shown to users and never derived from input.</summary>
    public string StorageKey { get; private set; }

    public UserId UploadedBy { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    public static Result<StudentDocument> Create(
        SchoolId tenantId,
        StudentId studentId,
        DocumentType type,
        DocumentFormat format,
        string fileName,
        long sizeBytes,
        string storageKey,
        UserId uploadedBy,
        DateTimeOffset uploadedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        if (fileName.Length > FileNameMaxLength || storageKey.Length > StorageKeyMaxLength)
        {
            throw new ArgumentException("File name or storage key too long.");
        }

        if (!DocumentRules.Accepts(type, format))
        {
            return Result.Failure<StudentDocument>(DocumentErrors.UnsupportedType);
        }

        if (sizeBytes <= 0)
        {
            return Result.Failure<StudentDocument>(DocumentErrors.Empty);
        }

        if (sizeBytes > DocumentRules.MaxBytes(type))
        {
            return Result.Failure<StudentDocument>(DocumentErrors.TooLarge);
        }

        return Result.Success(new StudentDocument(
            EntityId.New<StudentDocumentId>(), tenantId, studentId, type, format, fileName, sizeBytes, storageKey, uploadedBy, uploadedAt));
    }
}

/// <summary>Accepted formats and size limits per document type (DEC-322).</summary>
public static class DocumentRules
{
    public const long HealthReportMaxBytes = 5 * 1024 * 1024;
    public const long PhotoMaxBytes = 2 * 1024 * 1024;

    /// <summary>Health reports per student; bounds storage use per student (photos are always one).</summary>
    public const int MaxHealthReportsPerStudent = 20;

    /// <summary>The largest upload of any type; the request size limit of the upload endpoint is derived from it.</summary>
    public const long MaxBytesOfAnyType = HealthReportMaxBytes;

    public static long MaxBytes(DocumentType type) => type switch
    {
        DocumentType.HealthReport => HealthReportMaxBytes,
        DocumentType.Photo => PhotoMaxBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static IReadOnlyList<DocumentFormat> Formats(DocumentType type) => type switch
    {
        DocumentType.HealthReport => [DocumentFormat.Pdf, DocumentFormat.Jpeg, DocumentFormat.Png],
        DocumentType.Photo => [DocumentFormat.Jpeg, DocumentFormat.Png, DocumentFormat.WebP],
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool Accepts(DocumentType type, DocumentFormat format) => Formats(type).Contains(format);

    /// <summary>The only content type a stored object is ever served with.</summary>
    public static string ContentType(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => "application/pdf",
        DocumentFormat.Jpeg => "image/jpeg",
        DocumentFormat.Png => "image/png",
        DocumentFormat.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The canonical extension a stored file name gets.</summary>
    public static string Extension(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => ".pdf",
        DocumentFormat.Jpeg => ".jpg",
        DocumentFormat.Png => ".png",
        DocumentFormat.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Extensions a file name may end with for the format (case-insensitive).</summary>
    public static IReadOnlyList<string> Extensions(DocumentFormat format) => format switch
    {
        DocumentFormat.Pdf => [".pdf"],
        DocumentFormat.Jpeg => [".jpg", ".jpeg"],
        DocumentFormat.Png => [".png"],
        DocumentFormat.WebP => [".webp"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

public static class DocumentErrors
{
    public static readonly Error NotFound = Error.NotFound("Document.NotFound", "Document not found.");

    public static readonly Error Empty = Error.Validation("Document.Empty", "The file is empty.");

    public static readonly Error TooLarge = Error.Validation("Document.TooLarge", "The file is larger than the limit of its type.");

    /// <summary>The content is not one of the formats accepted for the type (checked by magic bytes and structure).</summary>
    public static readonly Error UnsupportedType = Error.Validation("Document.UnsupportedType", "The file content is not an accepted format for this document type.");

    /// <summary>The file name's extension does not match the detected content (e.g. <c>photo.jpg.html</c>).</summary>
    public static readonly Error ExtensionMismatch = Error.Validation("Document.ExtensionMismatch", "The file name extension does not match its content.");

    /// <summary>A PDF with active content (JavaScript, launch actions, embedded files).</summary>
    public static readonly Error ActiveContent = Error.Validation("Document.ActiveContent", "The file contains active content.");

    public static readonly Error LimitReached = Error.BusinessRule("Document.LimitReached", "The student already has the maximum number of documents of this type.");
}
