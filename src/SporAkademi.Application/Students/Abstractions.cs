using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students;

public sealed record EmergencyContactDto(string Name, string Phone, string Relationship);

public sealed record GuardianAddressDto(string Line, string District, string City);

/// <param name="Invitation">The guardian's parent account (P3-02, DEC-382); "not invited" until the first invitation.</param>
public sealed record StudentGuardianDto(
    Guid GuardianId,
    GuardianRelationship Relationship,
    string FirstName,
    string LastName,
    string Phone,
    string? Email,
    GuardianInvitationDto Invitation);

/// <summary>
/// Full admin view of a student, including guardians (appendix D §2.7). Never sent to coaches (DEC-007). Birth date
/// and emergency contact are null only once <see cref="AnonymizedAt"/> is set (DEC-363).
/// </summary>
public sealed record StudentDto(
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
    IReadOnlyList<StudentGuardianDto> Guardians,
    DateTimeOffset? AnonymizedAt,
    uint Version);

/// <summary>A search result row: only what the list needs (NFR-PRV-01).</summary>
public sealed record StudentListItemDto(
    Guid Id,
    string FirstName,
    string LastName,
    DateOnly? BirthDate,
    Gender Gender,
    StudentStatus Status,
    DateOnly? LeftAt,
    DateTimeOffset? AnonymizedAt);

public sealed record GuardianDto(
    Guid Id,
    string FirstName,
    string LastName,
    string Phone,
    string? Email,
    GuardianAddressDto? Address,
    uint Version);

public enum StudentSort
{
    LastName,
    LastNameDescending,
    CreatedAt,
    CreatedAtDescending,
}

/// <param name="SearchKey">Folded with <see cref="TextNormalization.SearchKey"/>; matches anywhere in "first last".</param>
/// <param name="OnlyStudents">Restriction from the group/sport filter; <c>null</c> means none.</param>
public sealed record StudentSearch(string? SearchKey, StudentStatus? Status, IReadOnlyCollection<StudentId>? OnlyStudents, StudentSort Sort);

/// <summary>Students of the current school (tenant filter applies).</summary>
public interface IStudentRepository
{
    /// <summary>The aggregate with its guardian links.</summary>
    Task<Student?> GetAsync(StudentId id, CancellationToken cancellationToken);

    void Add(Student student);

    Task<bool> ExistsAsync(StudentId id, CancellationToken cancellationToken);

    Task<bool> IsAnonymizedAsync(StudentId id, CancellationToken cancellationToken);

    /// <summary>Optimistic concurrency: the save fails with Concurrency.Conflict if the row changed since <paramref name="version"/>.</summary>
    void ExpectVersion(Student student, uint version);

    Task<StudentDto?> FindDtoAsync(StudentId id, CancellationToken cancellationToken);

    Task<PagedResult<StudentListItemDto>> SearchAsync(StudentSearch search, PageRequest page, CancellationToken cancellationToken);

    /// <summary>
    /// Students due for anonymization (DEC-362): left on or before <paramref name="cutoff"/> and not anonymized yet,
    /// in id order, except <paramref name="exclude"/>, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<StudentId>> ListDueForAnonymizationAsync(
        DateOnly cutoff,
        IReadOnlyCollection<StudentId> exclude,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>How many students <see cref="ListDueForAnonymizationAsync"/> would find for <paramref name="cutoff"/>.</summary>
    Task<int> CountDueForAnonymizationAsync(DateOnly cutoff, CancellationToken cancellationToken);

    /// <summary>The aggregates with their guardian links, tracked; unknown ids are omitted.</summary>
    Task<IReadOnlyList<Student>> GetManyAsync(IReadOnlyCollection<StudentId> ids, CancellationToken cancellationToken);
}

/// <summary>Guardians of the current school (tenant filter applies, so another school's id is not found).</summary>
public interface IGuardianRepository
{
    Task<Guardian?> GetAsync(GuardianId id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(GuardianId id, CancellationToken cancellationToken);

    Task<GuardianId?> FindIdByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken);

    Task<bool> PhoneExistsAsync(PhoneNumber phone, GuardianId? except, CancellationToken cancellationToken);

    void Add(Guardian guardian);

    /// <summary>Hard delete (DEC-365): only for a guardian no student is linked to any more.</summary>
    void Remove(Guardian guardian);

    /// <summary>Whether any student of the school is linked to the guardian (saved links only).</summary>
    Task<bool> IsLinkedToAnyStudentAsync(GuardianId id, CancellationToken cancellationToken);

    /// <summary>
    /// DEC-368: <c>FOR SHARE</c> on the guardian row until the transaction ends, before a student is linked to it.
    /// A concurrent deletion either waits for the link (and then keeps the guardian) or wins first (and the guardian
    /// is then no longer found).
    /// </summary>
    Task LockForLinkAsync(GuardianId id, CancellationToken cancellationToken);

    /// <summary>DEC-368: <c>FOR UPDATE</c> on the guardian row before checking it has no students and deleting it.</summary>
    Task LockForDeletionAsync(GuardianId id, CancellationToken cancellationToken);

    /// <summary>Guardians no student of the school is linked to, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<GuardianId>> ListUnlinkedAsync(int limit, CancellationToken cancellationToken);

    void ExpectVersion(Guardian guardian, uint version);

    /// <summary>DEC-383: whether an active (hence not anonymized) student of the school is linked to the guardian.</summary>
    Task<bool> HasActiveStudentAsync(GuardianId id, CancellationToken cancellationToken);

    /// <summary>The guardians linked to the student (saved links).</summary>
    Task<IReadOnlyList<GuardianId>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken);

    /// <summary>The parent account of each of <paramref name="ids"/> that has one (DEC-381).</summary>
    Task<IReadOnlyDictionary<GuardianId, Guid>> AccountUserIdsAsync(IReadOnlyCollection<GuardianId> ids, CancellationToken cancellationToken);

    Task<GuardianDto?> FindDtoAsync(GuardianId id, CancellationToken cancellationToken);

    Task<GuardianDto?> FindDtoByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken);
}
