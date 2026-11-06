using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students;

public sealed record RegisterStudentCommand(
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    string? Phone,
    string? Email,
    EmergencyContactInput? EmergencyContact,
    IReadOnlyList<GuardianLinkInput>? Guardians) : ICommand<StudentId>, IStudentFields, IGuardianLinks;

public sealed record UpdateStudentCommand(
    StudentId Id,
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    string? Phone,
    string? Email,
    EmergencyContactInput? EmergencyContact,
    uint Version) : ICommand<Unit>, IStudentFields;

public sealed record ReplaceStudentGuardiansCommand(StudentId Id, IReadOnlyList<GuardianLinkInput>? Guardians, uint Version) : ICommand<Unit>, IGuardianLinks;

public sealed record LeaveStudentCommand(StudentId Id, DateOnly? Date) : ICommand<Unit>;

public sealed record ReactivateStudentCommand(StudentId Id) : ICommand<Unit>;

public sealed record GetStudentQuery(StudentId Id) : IQuery<StudentDto>;

/// <param name="Status">Active or Left; all statuses when empty.</param>
/// <param name="Sort">lastName (default), -lastName, createdAt, -createdAt.</param>
public sealed record SearchStudentsQuery(
    string? Search,
    string? Status,
    Guid? GroupId,
    Guid? SportId,
    string? Sort,
    PageRequest Page) : IQuery<PagedResult<StudentListItemDto>>
{
    public const int SearchMaxLength = 100;

    public static readonly IReadOnlyDictionary<string, StudentSort> SortFields = new Dictionary<string, StudentSort>(StringComparer.Ordinal)
    {
        ["lastName"] = StudentSort.LastName,
        ["-lastName"] = StudentSort.LastNameDescending,
        ["createdAt"] = StudentSort.CreatedAt,
        ["-createdAt"] = StudentSort.CreatedAtDescending,
    };
}

internal sealed class RegisterStudentValidator : StudentFieldsValidator<RegisterStudentCommand>
{
    public RegisterStudentValidator() => this.AddGuardianLinkRules();
}

internal sealed class UpdateStudentValidator : StudentFieldsValidator<UpdateStudentCommand>
{
    public UpdateStudentValidator() => RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode("Field.Required");
}

internal sealed class ReplaceStudentGuardiansValidator : AbstractValidator<ReplaceStudentGuardiansCommand>
{
    public ReplaceStudentGuardiansValidator()
    {
        this.AddGuardianLinkRules();
        RuleFor(c => c.Version).GreaterThan(0u).WithErrorCode("Field.Required");
    }
}

internal sealed class LeaveStudentValidator : AbstractValidator<LeaveStudentCommand>
{
    public LeaveStudentValidator() => RuleFor(c => c.Date).NotNull().WithErrorCode("Field.Required");
}

internal sealed class SearchStudentsValidator : AbstractValidator<SearchStudentsQuery>
{
    public SearchStudentsValidator()
    {
        RuleFor(q => q.Search).MaximumLength(SearchStudentsQuery.SearchMaxLength).WithErrorCode("Field.TooLong");
        RuleFor(q => q.Status).IsEnumName(typeof(StudentStatus), caseSensitive: false).WithErrorCode("Field.Invalid")
            .When(q => !string.IsNullOrEmpty(q.Status));
        RuleFor(q => q.Sort).Must(s => SearchStudentsQuery.SortFields.ContainsKey(s!)).WithErrorCode("Field.Invalid")
            .When(q => !string.IsNullOrEmpty(q.Sort));

        // Paging errors use the query-string names (page, pageSize).
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
    }
}

internal sealed class RegisterStudentHandler(
    IStudentRepository students,
    IGuardianRepository guardians,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : ICommandHandler<RegisterStudentCommand, StudentId>
{
    public async Task<Result<StudentId>> Handle(RegisterStudentCommand command, CancellationToken cancellationToken)
    {
        var details = command.ToDetails();
        if (details.IsFailure)
        {
            return Result.Failure<StudentId>(details.Error);
        }

        var tenant = currentUser.RequireTenant();
        var links = await GuardianLinkResolver.ResolveAsync(command.Guardians ?? [], tenant, guardians, cancellationToken);
        if (links.IsFailure)
        {
            return Result.Failure<StudentId>(links.Error);
        }

        var today = await tenancy.GetTodayAsync(tenant, cancellationToken);
        var registered = Student.Register(tenant, details.Value, links.Value, today);
        if (registered.IsFailure)
        {
            return Result.Failure<StudentId>(registered.Error.ToStudentFieldError());
        }

        students.Add(registered.Value);
        return Result.Success(registered.Value.Id);
    }
}

internal sealed class UpdateStudentHandler(IStudentRepository students, ITenancyModule tenancy, ICurrentUser currentUser)
    : ICommandHandler<UpdateStudentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(UpdateStudentCommand command, CancellationToken cancellationToken)
    {
        var student = await students.GetAsync(command.Id, cancellationToken);
        if (student is null)
        {
            return Result.Failure<Unit>(StudentErrors.NotFound);
        }

        var details = command.ToDetails();
        if (details.IsFailure)
        {
            return Result.Failure<Unit>(details.Error);
        }

        var today = await tenancy.GetTodayAsync(currentUser.RequireTenant(), cancellationToken);
        students.ExpectVersion(student, command.Version);
        var updated = student.Update(details.Value, today);
        return updated.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(updated.Error.ToStudentFieldError());
    }
}

internal sealed class ReplaceStudentGuardiansHandler(
    IStudentRepository students,
    IGuardianRepository guardians,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : ICommandHandler<ReplaceStudentGuardiansCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ReplaceStudentGuardiansCommand command, CancellationToken cancellationToken)
    {
        var student = await students.GetAsync(command.Id, cancellationToken);
        if (student is null)
        {
            return Result.Failure<Unit>(StudentErrors.NotFound);
        }

        var tenant = currentUser.RequireTenant();
        var links = await GuardianLinkResolver.ResolveAsync(command.Guardians ?? [], tenant, guardians, cancellationToken);
        if (links.IsFailure)
        {
            return Result.Failure<Unit>(links.Error);
        }

        var today = await tenancy.GetTodayAsync(tenant, cancellationToken);
        students.ExpectVersion(student, command.Version);
        var replaced = student.ReplaceGuardians(links.Value, today);
        return replaced.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(replaced.Error.ToGuardiansFieldError());
    }
}

internal sealed class LeaveStudentHandler(IStudentRepository students, ITenancyModule tenancy, ICurrentUser currentUser)
    : ICommandHandler<LeaveStudentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(LeaveStudentCommand command, CancellationToken cancellationToken)
    {
        var student = await students.GetAsync(command.Id, cancellationToken);
        if (student is null)
        {
            return Result.Failure<Unit>(StudentErrors.NotFound);
        }

        // A future date would mark the student Left while enrollments stay active until then.
        var date = command.Date!.Value;
        var today = await tenancy.GetTodayAsync(currentUser.RequireTenant(), cancellationToken);
        if (date > today || date < student.BirthDate)
        {
            return Result.Failure<Unit>(ValidationErrors.ForField("date", CommonErrors.FieldInvalid));
        }

        var left = student.Leave(date);
        return left.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(left.Error);
    }
}

internal sealed class ReactivateStudentHandler(IStudentRepository students) : ICommandHandler<ReactivateStudentCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ReactivateStudentCommand command, CancellationToken cancellationToken)
    {
        var student = await students.GetAsync(command.Id, cancellationToken);
        if (student is null)
        {
            return Result.Failure<Unit>(StudentErrors.NotFound);
        }

        var reactivated = student.Reactivate();
        return reactivated.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(reactivated.Error);
    }
}

internal sealed class GetStudentHandler(IStudentRepository students) : IQueryHandler<GetStudentQuery, StudentDto>
{
    public async Task<Result<StudentDto>> Handle(GetStudentQuery query, CancellationToken cancellationToken) =>
        await students.FindDtoAsync(query.Id, cancellationToken) is { } dto
            ? Result.Success(dto)
            : Result.Failure<StudentDto>(StudentErrors.NotFound);
}

internal sealed class SearchStudentsHandler(IStudentRepository students, IStudentGroupFilter groupFilter)
    : IQueryHandler<SearchStudentsQuery, PagedResult<StudentListItemDto>>
{
    public async Task<Result<PagedResult<StudentListItemDto>>> Handle(SearchStudentsQuery query, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(query.Search) ? null : TextNormalization.SearchKey(query.Search);
        StudentStatus? status = string.IsNullOrEmpty(query.Status) ? null : Enum.Parse<StudentStatus>(query.Status, ignoreCase: true);
        var sort = string.IsNullOrEmpty(query.Sort) ? StudentSort.LastName : SearchStudentsQuery.SortFields[query.Sort];
        var only = query.GroupId is null && query.SportId is null
            ? null
            : await groupFilter.StudentsEnrolledInAsync(query.GroupId, query.SportId is { } sport ? SportId.From(sport) : null, cancellationToken);

        return Result.Success(await students.SearchAsync(new StudentSearch(key, status, only, sort), query.Page, cancellationToken));
    }
}

/// <summary>US-15: a student who leaves has every active enrollment ended on the leave date (appendix B §6).</summary>
internal sealed class EndEnrollmentsWhenStudentLeaves(IEnrollmentEnder enrollments) : IDomainEventConsumer<StudentLeft>
{
    public async Task<Result> Handle(StudentLeft domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await enrollments.EndActiveEnrollmentsOfStudentAsync(domainEvent.StudentId, domainEvent.LeftAt, cancellationToken);
        return Result.Success();
    }
}
