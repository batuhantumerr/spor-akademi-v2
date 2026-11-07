using System.Security.Cryptography;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Storage;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Students.Documents;

/// <summary>A stored document as the School Admin sees it. The storage key never leaves the server.</summary>
public sealed record StudentDocumentDto(
    Guid Id,
    Guid StudentId,
    DocumentType Type,
    string ContentType,
    string FileName,
    long SizeBytes,
    Guid UploadedBy,
    DateTimeOffset UploadedAt);

/// <summary>A short-lived signed link to view or download one document (DEC-323).</summary>
public sealed record DocumentLinkDto(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Documents of the current school (tenant filter applies, so another school's id is not found).</summary>
public interface IStudentDocumentRepository
{
    void Add(StudentDocument document);

    void Remove(StudentDocument document);

    Task<StudentDocument?> FindAsync(StudentDocumentId id, CancellationToken cancellationToken);

    Task<StudentDocument?> FindPhotoAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<int> CountAsync(StudentId studentId, DocumentType type, CancellationToken cancellationToken);

    /// <summary>Photo first, then health reports newest first.</summary>
    Task<IReadOnlyList<StudentDocument>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken);
}

/// <param name="Type">The document type's name (<see cref="DocumentType"/>), as sent in the form.</param>
/// <param name="FileName">The client's file name: display data only, checked against the content.</param>
public sealed record UploadStudentDocumentCommand(StudentId StudentId, string? Type, string? FileName, byte[]? Content)
    : ICommand<StudentDocumentDto>, IManagesOwnTransaction;

public sealed record DeleteStudentDocumentCommand(StudentDocumentId Id) : ICommand<Unit>, IManagesOwnTransaction;

public sealed record ListStudentDocumentsQuery(StudentId StudentId) : IQuery<IReadOnlyList<StudentDocumentDto>>;

/// <summary>A link to any document of the school; School Admin only.</summary>
public sealed record GetDocumentLinkQuery(StudentDocumentId Id) : IQuery<DocumentLinkDto>;

/// <summary>A link to the student's photo: the School Admin, or a coach of one of the student's active groups.</summary>
public sealed record GetStudentPhotoLinkQuery(StudentId StudentId) : IQuery<DocumentLinkDto>;

internal sealed class UploadStudentDocumentValidator : AbstractValidator<UploadStudentDocumentCommand>
{
    public UploadStudentDocumentValidator()
    {
        RuleFor(c => c.Type).NotEmpty().WithErrorCode("Field.Required")
            .Must(t => Enum.TryParse<DocumentType>(t, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(t, out _))
            .WithErrorCode("Field.Invalid");
        RuleFor(c => c.Content).NotNull().WithErrorCode("Field.Required").OverridePropertyName("file");
        RuleFor(c => c.FileName).MaximumLength(255).WithErrorCode("Field.TooLong").OverridePropertyName("file");
    }
}

/// <summary>Object keys: <c>{school}/{student}/{128 random bits}</c>; never derived from user input (DEC-322).</summary>
internal static class DocumentKeys
{
    public static string New(SchoolId school, StudentId student) =>
        $"{school.Value:N}/{student.Value:N}/{RandomNumberGenerator.GetHexString(32, lowercase: true)}";
}

internal static class DocumentViews
{
    public static StudentDocumentDto ToDto(StudentDocument d) => new(
        d.Id.Value,
        d.StudentId.Value,
        d.Type,
        DocumentRules.ContentType(d.Format),
        d.FileName,
        d.SizeBytes,
        d.UploadedBy.Value,
        d.UploadedAt);

    /// <summary>
    /// Images are shown inline; a PDF is always downloaded, never rendered on the storage origin. The download name is
    /// generic (<c>saglik-raporu.pdf</c>, <c>fotograf.jpg</c>): the signed disposition is part of the link's query
    /// string, and a user's file name (often a person's name) must not end up in URLs or storage access logs (DEC-323).
    /// </summary>
    public static DocumentLinkDto Link(IDocumentStorage storage, StudentDocument d)
    {
        var name = (d.Type == DocumentType.Photo ? "fotograf" : "saglik-raporu") + DocumentRules.Extension(d.Format);
        var link = storage.CreateDownloadLink(
            d.StorageKey,
            new DownloadDisposition(DocumentRules.ContentType(d.Format), name, Inline: d.Format != DocumentFormat.Pdf));
        return new DocumentLinkDto(link.Url, link.ExpiresAt);
    }
}

/// <summary>
/// Upload (DEC-321): the bytes come through the API, are inspected and cleaned, written to storage under a new key,
/// then the metadata is committed. If the database part fails the new object is deleted again; a replaced photo's
/// object is deleted after the commit.
/// </summary>
internal sealed partial class UploadStudentDocumentHandler(
    IStudentRepository students,
    IStudentDocumentRepository documents,
    IDocumentStorage storage,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<UploadStudentDocumentHandler> logger) : ICommandHandler<UploadStudentDocumentCommand, StudentDocumentDto>
{
    public async Task<Result<StudentDocumentDto>> Handle(UploadStudentDocumentCommand command, CancellationToken cancellationToken)
    {
        if (!await students.ExistsAsync(command.StudentId, cancellationToken))
        {
            return Result.Failure<StudentDocumentDto>(StudentErrors.NotFound);
        }

        var type = Enum.Parse<DocumentType>(command.Type!);
        var inspected = DocumentContent.Inspect(type, command.FileName, command.Content!);
        if (inspected.IsFailure)
        {
            return Result.Failure<StudentDocumentDto>(inspected.Error);
        }

        if (type == DocumentType.HealthReport
            && await documents.CountAsync(command.StudentId, type, cancellationToken) >= DocumentRules.MaxHealthReportsPerStudent)
        {
            return Result.Failure<StudentDocumentDto>(DocumentErrors.LimitReached.WithArgument("max", DocumentRules.MaxHealthReportsPerStudent));
        }

        var file = inspected.Value;
        var tenant = currentUser.RequireTenant();
        var uploader = currentUser.UserId ?? throw new InvalidOperationException("Uploading needs a signed-in user.");
        var key = DocumentKeys.New(tenant, command.StudentId);
        await storage.PutAsync(key, file.Content, DocumentRules.ContentType(file.Format), cancellationToken);

        string? replacedKey = null;
        Result<StudentDocumentDto> saved;
        try
        {
            saved = await unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    replacedKey = null;
                    var created = StudentDocument.Create(
                        tenant, command.StudentId, type, file.Format, file.FileName, file.Content.LongLength, key, UserId.From(uploader), timeProvider.GetUtcNow());
                    if (created.IsFailure)
                    {
                        return Result.Failure<StudentDocumentDto>(created.Error);
                    }

                    if (type == DocumentType.Photo && await documents.FindPhotoAsync(command.StudentId, ct) is { } previous)
                    {
                        documents.Remove(previous);
                        replacedKey = previous.StorageKey;
                    }

                    documents.Add(created.Value);
                    return Result.Success(DocumentViews.ToDto(created.Value));
                },
                cancellationToken);
        }
        catch
        {
            await TryDeleteAsync(key);
            throw;
        }

        if (saved.IsFailure)
        {
            await TryDeleteAsync(key);
            return saved;
        }

        if (replacedKey is not null)
        {
            await TryDeleteAsync(replacedKey);
        }

        return saved;
    }

    private async Task TryDeleteAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
#pragma warning disable CA1031 // A failed clean-up must not hide the outcome; the orphan is logged (by key, no personal data).
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogOrphan(logger, key, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete stored object {StorageKey}; it is orphaned")]
    private static partial void LogOrphan(ILogger logger, string storageKey, Exception exception);
}

/// <summary>Hard delete (DEC-325): the metadata row is deleted and committed, then the object.</summary>
internal sealed partial class DeleteStudentDocumentHandler(
    IStudentDocumentRepository documents,
    IDocumentStorage storage,
    IUnitOfWork unitOfWork,
    ILogger<DeleteStudentDocumentHandler> logger) : ICommandHandler<DeleteStudentDocumentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteStudentDocumentCommand command, CancellationToken cancellationToken)
    {
        string? key = null;
        var deleted = await unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (await documents.FindAsync(command.Id, ct) is not { } document)
                {
                    return Result.Failure<Unit>(DocumentErrors.NotFound);
                }

                key = document.StorageKey;
                documents.Remove(document);
                return Result.Success(Unit.Value);
            },
            cancellationToken);
        if (deleted.IsFailure || key is null)
        {
            return deleted;
        }

        try
        {
            await storage.DeleteAsync(key, CancellationToken.None);
        }
#pragma warning disable CA1031 // The row is gone; the caller's delete succeeded. The orphaned object is logged by key only.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogOrphan(logger, key, exception);
        }

        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete stored object {StorageKey} of a deleted document; it is orphaned")]
    private static partial void LogOrphan(ILogger logger, string storageKey, Exception exception);
}

internal sealed class ListStudentDocumentsHandler(IStudentRepository students, IStudentDocumentRepository documents)
    : IQueryHandler<ListStudentDocumentsQuery, IReadOnlyList<StudentDocumentDto>>
{
    public async Task<Result<IReadOnlyList<StudentDocumentDto>>> Handle(ListStudentDocumentsQuery query, CancellationToken cancellationToken)
    {
        if (!await students.ExistsAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<StudentDocumentDto>>(StudentErrors.NotFound);
        }

        var all = await documents.ListOfStudentAsync(query.StudentId, cancellationToken);
        return Result.Success<IReadOnlyList<StudentDocumentDto>>([.. all.Select(DocumentViews.ToDto)]);
    }
}

internal sealed class GetDocumentLinkHandler(IStudentDocumentRepository documents, IDocumentStorage storage)
    : IQueryHandler<GetDocumentLinkQuery, DocumentLinkDto>
{
    public async Task<Result<DocumentLinkDto>> Handle(GetDocumentLinkQuery query, CancellationToken cancellationToken) =>
        await documents.FindAsync(query.Id, cancellationToken) is { } document
            ? Result.Success(DocumentViews.Link(storage, document))
            : Result.Failure<DocumentLinkDto>(DocumentErrors.NotFound);
}

/// <summary>
/// DEC-324: a coach sees the photo of a student actively enrolled in one of the coach's active groups; every other
/// student is "not found" for the coach, exactly like an unknown id. Health reports are never reachable by coaches.
/// </summary>
internal sealed class GetStudentPhotoLinkHandler(
    IStudentRepository students,
    IStudentDocumentRepository documents,
    IDocumentStorage storage,
    IEnrollmentHistory enrollments,
    ICoachGroups coachGroups,
    ICurrentUser currentUser) : IQueryHandler<GetStudentPhotoLinkQuery, DocumentLinkDto>
{
    public async Task<Result<DocumentLinkDto>> Handle(GetStudentPhotoLinkQuery query, CancellationToken cancellationToken)
    {
        if (!await students.ExistsAsync(query.StudentId, cancellationToken) || !await IsVisibleAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<DocumentLinkDto>(StudentErrors.NotFound);
        }

        return await documents.FindPhotoAsync(query.StudentId, cancellationToken) is { } photo
            ? Result.Success(DocumentViews.Link(storage, photo))
            : Result.Failure<DocumentLinkDto>(DocumentErrors.NotFound);
    }

    private async Task<bool> IsVisibleAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        if (currentUser.Role != Role.Coach)
        {
            return true;
        }

        if (currentUser.UserId is not { } me)
        {
            return false;
        }

        var mine = (await coachGroups.ListActiveGroupIdsAsync(UserId.From(me), cancellationToken)).ToHashSet();
        var periods = await enrollments.ListOfStudentAsync(studentId, cancellationToken);
        return periods.Any(p => p.EndDate is null && mine.Contains(p.GroupId));
    }
}
