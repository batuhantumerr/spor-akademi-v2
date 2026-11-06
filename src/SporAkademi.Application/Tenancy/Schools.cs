using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Tenancy;

public sealed record NewSchoolAdmin(string FirstName, string LastName, string Email);

/// <summary>US-01: a school and its first School Admin, atomically (Platform Admin only).</summary>
public sealed record CreateSchoolCommand(string Name, string Slug, string TimeZone, NewSchoolAdmin Admin) : ICommand<SchoolCreated>;

public sealed record SchoolCreated(Guid SchoolId, Guid AdminId, string TemporaryPassword);

public sealed record SchoolDto(Guid Id, string Name, string Slug, string TimeZone, SchoolStatus Status);

public sealed record SchoolListItem(Guid Id, string Name, string Slug, string TimeZone, SchoolStatus Status, SchoolAdminSummary? Admin);

public sealed record GetSchoolQuery(Guid Id) : IQuery<SchoolDto>;

public sealed record ListSchoolsQuery(PageRequest Page) : IQuery<PagedResult<SchoolListItem>>;

/// <summary>Schools are platform-level, not tenant-scoped; only the Platform Admin reaches these use cases.</summary>
public interface ISchoolRepository
{
    Task<bool> SlugExistsAsync(Slug slug, CancellationToken cancellationToken);

    void Add(School school);

    Task<SchoolDto?> FindDtoAsync(SchoolId id, CancellationToken cancellationToken);

    /// <summary>Schools by name.</summary>
    Task<PagedResult<SchoolDto>> ListAsync(PageRequest page, CancellationToken cancellationToken);
}

internal sealed class CreateSchoolValidator : AbstractValidator<CreateSchoolCommand>
{
    public CreateSchoolValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .MaximumLength(School.NameMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
        RuleFor(c => c.Slug).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(s => Slug.Create(s).IsSuccess).WithErrorCode(Slug.Invalid.Code);
        RuleFor(c => c.TimeZone).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(School.IsKnownTimeZone).WithErrorCode(SchoolErrors.InvalidTimeZone.Code);
        RuleFor(c => c.Admin).NotNull().WithErrorCode(CommonErrors.FieldRequired.Code);
        When(c => c.Admin is not null, () =>
        {
            RuleFor(c => c.Admin.FirstName).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
                .MaximumLength(User.NameMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
            RuleFor(c => c.Admin.LastName).NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
                .MaximumLength(User.NameMaxLength).WithErrorCode(CommonErrors.FieldTooLong.Code);
            RuleFor(c => c.Admin.Email).Cascade(CascadeMode.Stop)
                .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
                .Must(e => Email.Create(e).IsSuccess).WithErrorCode(Email.Invalid.Code);
        });
    }
}

internal sealed class ListSchoolsValidator : AbstractValidator<ListSchoolsQuery>
{
    public ListSchoolsValidator() => RuleFor(q => q.Page).SetValidator(new PageRequestValidator());
}

internal sealed class CreateSchoolHandler(ISchoolRepository schools, IIdentityModule identity) : ICommandHandler<CreateSchoolCommand, SchoolCreated>
{
    public async Task<Result<SchoolCreated>> Handle(CreateSchoolCommand command, CancellationToken cancellationToken)
    {
        var slug = Slug.Create(command.Slug);
        if (slug.IsFailure)
        {
            return Result.Failure<SchoolCreated>(slug.Error);
        }

        if (await schools.SlugExistsAsync(slug.Value, cancellationToken))
        {
            return Result.Failure<SchoolCreated>(SchoolErrors.SlugTaken);
        }

        var school = School.Create(command.Name, slug.Value, command.TimeZone, RetentionPolicy.Default);
        if (school.IsFailure)
        {
            return Result.Failure<SchoolCreated>(school.Error);
        }

        schools.Add(school.Value);

        // Same transaction: a failure here (e.g. User.EmailTaken) rolls the school back too.
        var admin = await identity.CreateSchoolAdminAsync(
            school.Value.Id, command.Admin.FirstName, command.Admin.LastName, command.Admin.Email, cancellationToken);
        return admin.IsSuccess
            ? Result.Success(new SchoolCreated(school.Value.Id.Value, admin.Value.UserId, admin.Value.TemporaryPassword))
            : Result.Failure<SchoolCreated>(admin.Error);
    }
}

internal sealed class GetSchoolHandler(ISchoolRepository schools) : IQueryHandler<GetSchoolQuery, SchoolDto>
{
    public async Task<Result<SchoolDto>> Handle(GetSchoolQuery query, CancellationToken cancellationToken) =>
        await schools.FindDtoAsync(SchoolId.From(query.Id), cancellationToken) is { } school
            ? Result.Success(school)
            : Result.Failure<SchoolDto>(SchoolErrors.NotFound);
}

internal sealed class ListSchoolsHandler(ISchoolRepository schools, IIdentityModule identity)
    : IQueryHandler<ListSchoolsQuery, PagedResult<SchoolListItem>>
{
    public async Task<Result<PagedResult<SchoolListItem>>> Handle(ListSchoolsQuery query, CancellationToken cancellationToken)
    {
        var page = await schools.ListAsync(query.Page, cancellationToken);
        var admins = await identity.GetFirstSchoolAdminsAsync([.. page.Items.Select(s => SchoolId.From(s.Id))], cancellationToken);
        var items = page.Items
            .Select(s => new SchoolListItem(s.Id, s.Name, s.Slug, s.TimeZone, s.Status, admins.GetValueOrDefault(SchoolId.From(s.Id))))
            .ToList();
        return Result.Success(new PagedResult<SchoolListItem>(items, page.Page, page.PageSize, page.TotalCount));
    }
}
