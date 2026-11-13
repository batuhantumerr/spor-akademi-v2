using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Auditing;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Storage;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Students.Documents;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students.Privacy;

/// <summary>Why a student's data is erased before its retention period ends (P4-06, DEC-541). A code, never free text.</summary>
public enum ErasureReason
{
    /// <summary>The data subject (or, for a minor, a guardian) asked for erasure (KVKK md. 11/1-e).</summary>
    DataSubjectRequest,

    /// <summary>The consent the data was processed on was withdrawn.</summary>
    ConsentWithdrawn,

    /// <summary>The data was entered by mistake or for the wrong person.</summary>
    RecordedInError,
}

public sealed record ExportedStudentDto(
    Guid Id,
    string FirstName,
    string LastName,
    DateOnly? BirthDate,
    Gender Gender,
    StudentStatus Status,
    DateOnly? LeftAt,
    string? Phone,
    string? Email,
    EmergencyContactDto? EmergencyContact,
    DateTimeOffset? AnonymizedAt);

public sealed record ExportedGuardianDto(
    Guid Id,
    GuardianRelationship Relationship,
    string FirstName,
    string LastName,
    string Phone,
    string? Email,
    GuardianAddressDto? Address);

public sealed record ExportedEnrollmentDto(Guid Id, Guid GroupId, string GroupName, DateOnly StartDate, DateOnly? EndDate, EnrollmentStatus Status);

public sealed record ExportedAttendanceDto(DateOnly Date, TimeOnly StartTime, Guid GroupId, string GroupName, AttendanceStatus Status);

/// <param name="CancelReason">The School Admin's free text when the payment was cancelled (DEC-367): kept with the payment.</param>
public sealed record ExportedPaymentDto(
    Guid Id,
    Guid EnrollmentId,
    string Period,
    MoneyDto Amount,
    PaymentMethod Method,
    DateOnly PaidOn,
    PaymentStatus Status,
    string? CancelReason,
    DateTimeOffset? CancelledAt);

/// <summary>A refund of an online payment (P3-13); the reason and the resolution note are free text written by the school.</summary>
public sealed record ExportedRefundDto(
    Guid Id,
    Guid PaymentId,
    MoneyDto Amount,
    PaymentRefundStatus Status,
    string Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? ResolutionNote);

/// <summary>An online payment attempt (P2-F1), finished or not; provider tokens and links are left out.</summary>
public sealed record ExportedOnlinePaymentDto(
    Guid Id,
    Guid EnrollmentId,
    string Period,
    MoneyDto Amount,
    string Provider,
    OnlinePaymentStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <param name="DownloadUrl">A short-lived signed link (DEC-323, DEC-539); the file itself is not in the export.</param>
public sealed record ExportedDocumentDto(
    Guid Id,
    DocumentType Type,
    string ContentType,
    string FileName,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    Uri DownloadUrl,
    DateTimeOffset LinkExpiresAt);

/// <summary>
/// A machine-readable copy of one student's data and its guardians (P4-06, NFR-PRV-05, KVKK md. 11/1-b). The
/// <see cref="Format"/> names the shape, so a later version can change it without breaking a reader.
/// </summary>
public sealed record StudentDataExportDto(
    string Format,
    DateTimeOffset ExportedAt,
    ExportedStudentDto Student,
    IReadOnlyList<ExportedGuardianDto> Guardians,
    IReadOnlyList<ExportedEnrollmentDto> Enrollments,
    IReadOnlyList<ExportedAttendanceDto> Attendance,
    IReadOnlyList<ExportedPaymentDto> Payments,
    IReadOnlyList<ExportedRefundDto> Refunds,
    IReadOnlyList<ExportedOnlinePaymentDto> OnlinePayments,
    IReadOnlyList<ExportedDocumentDto> Documents)
{
    public const string FormatV1 = "spor-akademi.student-data-export/v1";
}

/// <summary>What other modules hold about one student, for the export; the school's rows only.</summary>
public interface IStudentDataExportReadModel
{
    Task<IReadOnlyList<ExportedEnrollmentDto>> EnrollmentsAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportedAttendanceDto>> AttendanceAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportedPaymentDto>> PaymentsAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportedRefundDto>> RefundsAsync(StudentId studentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportedOnlinePaymentDto>> OnlinePaymentsAsync(StudentId studentId, CancellationToken cancellationToken);
}

public sealed record GetStudentDataExportQuery(StudentId Id) : IQuery<StudentDataExportDto>;

/// <param name="Reason">An <see cref="ErasureReason"/> name.</param>
public sealed record EraseStudentCommand(StudentId Id, string? Reason) : ICommand<Unit>;

internal sealed class EraseStudentValidator : AbstractValidator<EraseStudentCommand>
{
    public EraseStudentValidator() =>
        RuleFor(c => c.Reason).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(EnumNames.IsExact<ErasureReason>)
            .WithErrorCode(CommonErrors.FieldInvalid.Code);
}

/// <summary>
/// P4-06 (DEC-539): the export is assembled from the student's own module and, for enrollments, attendance and payments,
/// a read model over those modules. Documents are listed with signed links instead of being packed into the response,
/// so a large file never passes through the API's memory.
/// </summary>
internal sealed class GetStudentDataExportHandler(
    IStudentRepository students,
    IGuardianRepository guardians,
    IStudentDocumentRepository documents,
    IStudentDataExportReadModel others,
    Lazy<IDocumentStorage> storage,
    TimeProvider timeProvider) : IQueryHandler<GetStudentDataExportQuery, StudentDataExportDto>
{
    public async Task<Result<StudentDataExportDto>> Handle(GetStudentDataExportQuery query, CancellationToken cancellationToken)
    {
        if (await students.FindDtoAsync(query.Id, cancellationToken) is not { } student)
        {
            return Result.Failure<StudentDataExportDto>(StudentErrors.NotFound);
        }

        var exportedGuardians = new List<ExportedGuardianDto>();
        foreach (var link in student.Guardians)
        {
            if (await guardians.FindDtoAsync(GuardianId.From(link.GuardianId), cancellationToken) is { } guardian)
            {
                exportedGuardians.Add(new ExportedGuardianDto(
                    guardian.Id, link.Relationship, guardian.FirstName, guardian.LastName, guardian.Phone, guardian.Email, guardian.Address));
            }
        }

        var files = await documents.ListOfStudentAsync(query.Id, cancellationToken);
        var exportedDocuments = files.Select(d =>
        {
            var view = DocumentViews.ToDto(d);
            var link = DocumentViews.Link(storage.Value, d);
            return new ExportedDocumentDto(view.Id, view.Type, view.ContentType, view.FileName, view.SizeBytes, view.UploadedAt, link.Url, link.ExpiresAt);
        }).ToList();

        return Result.Success(new StudentDataExportDto(
            StudentDataExportDto.FormatV1,
            timeProvider.GetUtcNow(),
            new ExportedStudentDto(
                student.Id,
                student.FirstName,
                student.LastName,
                student.BirthDate,
                student.Gender,
                student.Status,
                student.LeftAt,
                student.Phone,
                student.Email,
                student.EmergencyContact,
                student.AnonymizedAt),
            exportedGuardians,
            await others.EnrollmentsAsync(query.Id, cancellationToken),
            await others.AttendanceAsync(query.Id, cancellationToken),
            await others.PaymentsAsync(query.Id, cancellationToken),
            await others.RefundsAsync(query.Id, cancellationToken),
            await others.OnlinePaymentsAsync(query.Id, cancellationToken),
            exportedDocuments));
    }
}

/// <summary>
/// P4-06 (NFR-PRV-05, DEC-540 – DEC-542): erasure on request, whatever the retention period, with the same result as the
/// nightly anonymization (DEC-363 – DEC-368): guardians no other student needs and the documents are deleted by the
/// <see cref="StudentAnonymized"/> consumers, payments stay for the legal period without a name. An active student
/// leaves today first, which ends its active enrollments today. The audit entry carries the reason code and the erased
/// field names, in the same transaction.
/// </summary>
internal sealed class EraseStudentHandler(
    IStudentRepository students,
    ITenancyModule tenancy,
    IStudentPaymentActivity payments,
    ICurrentUser currentUser,
    IAuditTrail auditTrail,
    TimeProvider timeProvider) : ICommandHandler<EraseStudentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(EraseStudentCommand command, CancellationToken cancellationToken)
    {
        var student = await students.GetAsync(command.Id, cancellationToken);
        if (student is null)
        {
            return Result.Failure<Unit>(StudentErrors.NotFound);
        }

        // DEC-546: a provider callback could still create or change a payment of the student; wait until it settles.
        if (!student.IsAnonymized && await payments.HasOpenActivityAsync(student.Id, cancellationToken))
        {
            return Result.Failure<Unit>(StudentErrors.OpenPaymentActivity);
        }

        var today = await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
        var erased = student.Erase(today, timeProvider.GetUtcNow());
        if (erased.IsFailure)
        {
            return Result.Failure<Unit>(erased.Error);
        }

        // The validated name, re-rendered from the enum: never the client's string (DEC-533).
        var reason = Enum.Parse<ErasureReason>(command.Reason!).ToString();
        auditTrail.Add(new AuditRecord(AuditAction.Erase, AuditEntityType.Student, student.Id.Value, [], reason));
        return Result.Success(Unit.Value);
    }
}
