using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students;

public sealed record EmergencyContactDto(string Name, string Phone, string Relationship);

public sealed record GuardianAddressDto(string Line, string District, string City);

public sealed record StudentGuardianDto(
    Guid GuardianId,
    GuardianRelationship Relationship,
    string FirstName,
    string LastName,
    string Phone,
    string? Email);

/// <summary>Full admin view of a student, including guardians (appendix D §2.7). Never sent to coaches (DEC-007).</summary>
public sealed record StudentDto(
    Guid Id,
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    StudentStatus Status,
    DateOnly? LeftAt,
    string? Phone,
    string? Email,
    EmergencyContactDto EmergencyContact,
    IReadOnlyList<StudentGuardianDto> Guardians,
    uint Version);

/// <summary>A search result row: only what the list needs (NFR-PRV-01).</summary>
public sealed record StudentListItemDto(
    Guid Id,
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    StudentStatus Status,
    DateOnly? LeftAt);

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

    /// <summary>Optimistic concurrency: the save fails with Concurrency.Conflict if the row changed since <paramref name="version"/>.</summary>
    void ExpectVersion(Student student, uint version);

    Task<StudentDto?> FindDtoAsync(StudentId id, CancellationToken cancellationToken);

    Task<PagedResult<StudentListItemDto>> SearchAsync(StudentSearch search, PageRequest page, CancellationToken cancellationToken);
}

/// <summary>Guardians of the current school (tenant filter applies, so another school's id is not found).</summary>
public interface IGuardianRepository
{
    Task<Guardian?> GetAsync(GuardianId id, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(GuardianId id, CancellationToken cancellationToken);

    Task<GuardianId?> FindIdByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken);

    Task<bool> PhoneExistsAsync(PhoneNumber phone, GuardianId? except, CancellationToken cancellationToken);

    void Add(Guardian guardian);

    void ExpectVersion(Guardian guardian, uint version);

    Task<GuardianDto?> FindDtoAsync(GuardianId id, CancellationToken cancellationToken);

    Task<GuardianDto?> FindDtoByPhoneAsync(PhoneNumber phone, CancellationToken cancellationToken);
}
