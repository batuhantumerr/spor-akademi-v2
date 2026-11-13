using System.Threading.RateLimiting;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Auditing;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Students;
using SporAkademi.Application.Students.Privacy;
using SporAkademi.Domain.Students;

namespace SporAkademi.Api.Endpoints.Students;

internal sealed record RegisterStudentRequest(
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    string? Phone,
    string? Email,
    EmergencyContactInput? EmergencyContact,
    IReadOnlyList<GuardianLinkInput>? Guardians);

internal sealed record UpdateStudentRequest(
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    string? Phone,
    string? Email,
    EmergencyContactInput? EmergencyContact,
    uint Version);

internal sealed record ReplaceGuardiansRequest(IReadOnlyList<GuardianLinkInput>? Guardians, uint Version);

internal sealed record LeaveStudentRequest(DateOnly? Date);

/// <param name="Reason">An erasure reason code (P4-06): DataSubjectRequest, ConsentWithdrawn or RecordedInError.</param>
internal sealed record EraseStudentRequest(string? Reason);

internal sealed record UpdateGuardianRequest(string FirstName, string LastName, string Phone, string? Email, GuardianAddressDto? Address, uint Version);

/// <summary>Students and guardians (appendix D §2.7, US-13, US-15, US-16). School Admin only.</summary>
internal static class StudentEndpoints
{
    /// <summary>
    /// DEC-388: invitations per School Admin (the token's subject) in 10 minutes, across all guardians. The per-account
    /// cap cannot see a guardian deleted and created again, or an address moved between guardians; this limit can.
    /// </summary>
    public const string InvitationRateLimitPolicy = "guardian-invitations";
    public const int InvitationsPerWindow = 10;
    public static readonly TimeSpan InvitationWindow = TimeSpan.FromMinutes(10);

    public static IServiceCollection AddInvitationRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(InvitationRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = InvitationsPerWindow, Window = InvitationWindow, QueueLimit = 0 })));

    public static IEndpointRouteBuilder MapStudentEndpoints(this IEndpointRouteBuilder app)
    {
        var students = app.MapGroup("/api/v1/students").WithTags("Students").RequireRole(Role.SchoolAdmin);
        students.MapGet("/", SearchStudents).Produces<PageResponse<StudentListItemDto>>().ProducesProblems(400)
            .AuditsList(AuditEntityType.Student, "search", "status", "groupId", "sportId", "sort");
        students.MapGet("/{id:guid}", GetStudent).Produces<StudentDto>().ProducesProblems(404).AuditsRead(AuditEntityType.Student);
        students.MapPost("/", RegisterStudent).Produces<StudentDto>(StatusCodes.Status201Created).ProducesProblems(400, 404, 409, 422);
        students.MapPut("/{id:guid}", UpdateStudent).Produces<StudentDto>().ProducesProblems(400, 404, 409, 422);
        students.MapPut("/{id:guid}/guardians", ReplaceGuardians).Produces<StudentDto>().ProducesProblems(400, 404, 409, 422);
        students.MapPost("/{id:guid}/leave", LeaveStudent).Produces<StudentDto>().ProducesProblems(400, 404, 422);
        students.MapPost("/{id:guid}/reactivate", ReactivateStudent).Produces<StudentDto>().ProducesProblems(404, 422);

        // P4-06 (NFR-PRV-05): data subject requests. Both are recorded in the audit log.
        students.MapGet("/{id:guid}/data-export", ExportStudentData).Produces<StudentDataExportDto>().ProducesProblems(404)
            .AuditsRead(AuditEntityType.Student, AuditAction.Export);
        students.MapPost("/{id:guid}/erasure", EraseStudent).Produces<StudentDto>().ProducesProblems(400, 404, 409, 422);

        var guardians = app.MapGroup("/api/v1/guardians").WithTags("Guardians").RequireRole(Role.SchoolAdmin);
        guardians.MapGet("/", FindGuardiansByPhone).Produces<PageResponse<GuardianDto>>().ProducesProblems(400)
            .AuditsList(AuditEntityType.Guardian, "phone");
        guardians.MapPut("/{id:guid}", UpdateGuardian).Produces<GuardianDto>().ProducesProblems(400, 404, 409);

        // P3-02 (DEC-383): the guardian's parent account invitation. POST invites, or sends it again (the earlier link
        // stops working); DELETE revokes it or closes the account.
        guardians.MapGet("/{id:guid}/invitation", GetInvitation).Produces<GuardianInvitationDto>().ProducesProblems(404);
        guardians.MapPost("/{id:guid}/invitation", Invite)
            .RequireRateLimiting(InvitationRateLimitPolicy)
            .Produces<GuardianInvitationDto>()
            .ProducesProblems(404, 409, 422, 429);
        guardians.MapDelete("/{id:guid}/invitation", RevokeInvitation).Produces<GuardianInvitationDto>().ProducesProblems(404, 422);

        return app;
    }

    private static async Task<IResult> GetInvitation(Guid id, IQueryHandler<GetGuardianInvitationQuery, GuardianInvitationDto> reader, CancellationToken cancellationToken) =>
        (await reader.Handle(new GetGuardianInvitationQuery(GuardianId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> Invite(
        Guid id,
        ICommandHandler<InviteGuardianCommand, Unit> handler,
        IQueryHandler<GetGuardianInvitationQuery, GuardianInvitationDto> reader,
        CancellationToken cancellationToken)
    {
        var invited = await handler.Handle(new InviteGuardianCommand(GuardianId.From(id)), cancellationToken);
        return invited.IsSuccess ? await GetInvitation(id, reader, cancellationToken) : ProblemResults.Problem(invited.Error);
    }

    private static async Task<IResult> RevokeInvitation(
        Guid id,
        ICommandHandler<RevokeGuardianInvitationCommand, Unit> handler,
        IQueryHandler<GetGuardianInvitationQuery, GuardianInvitationDto> reader,
        CancellationToken cancellationToken)
    {
        var revoked = await handler.Handle(new RevokeGuardianInvitationCommand(GuardianId.From(id)), cancellationToken);
        return revoked.IsSuccess ? await GetInvitation(id, reader, cancellationToken) : ProblemResults.Problem(revoked.Error);
    }

    private static async Task<IResult> SearchStudents(
        string? search,
        string? status,
        Guid? groupId,
        Guid? sportId,
        string? sort,
        int? page,
        int? pageSize,
        IQueryHandler<SearchStudentsQuery, PagedResult<StudentListItemDto>> handler,
        CancellationToken cancellationToken)
    {
        var query = new SearchStudentsQuery(search, status, groupId, sportId, sort, EndpointConventions.ToPageRequest(page, pageSize));
        var result = await handler.Handle(query, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<StudentListItemDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> GetStudent(Guid id, IQueryHandler<GetStudentQuery, StudentDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetStudentQuery(StudentId.From(id)), cancellationToken)).OkOrProblem();

    private static async Task<IResult> RegisterStudent(
        RegisterStudentRequest request,
        ICommandHandler<RegisterStudentCommand, StudentId> handler,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new RegisterStudentCommand(
            request.FirstName, request.LastName, request.BirthDate, request.Gender, request.Phone, request.Email, request.EmergencyContact, request.Guardians);
        var created = await GuardianLinkConflicts.RetryOnceAsync(() => handler.Handle(command, cancellationToken));
        if (created.IsFailure)
        {
            return ProblemResults.Problem(created.Error);
        }

        var student = await reader.Handle(new GetStudentQuery(created.Value), cancellationToken);
        return TypedResults.Created($"/api/v1/students/{created.Value.Value}", student.Value);
    }

    private static async Task<IResult> UpdateStudent(
        Guid id,
        UpdateStudentRequest request,
        ICommandHandler<UpdateStudentCommand, Unit> handler,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new UpdateStudentCommand(
            StudentId.From(id), request.FirstName, request.LastName, request.BirthDate, request.Gender, request.Phone, request.Email, request.EmergencyContact, request.Version);
        return await ThenRead(await handler.Handle(command, cancellationToken), id, reader, cancellationToken);
    }

    private static async Task<IResult> ReplaceGuardians(
        Guid id,
        ReplaceGuardiansRequest request,
        ICommandHandler<ReplaceStudentGuardiansCommand, Unit> handler,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new ReplaceStudentGuardiansCommand(StudentId.From(id), request.Guardians, request.Version);
        var replaced = await GuardianLinkConflicts.RetryOnceAsync(() => handler.Handle(command, cancellationToken));
        return await ThenRead(replaced, id, reader, cancellationToken);
    }

    private static async Task<IResult> LeaveStudent(
        Guid id,
        LeaveStudentRequest request,
        ICommandHandler<LeaveStudentCommand, Unit> handler,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken) =>
        await ThenRead(await handler.Handle(new LeaveStudentCommand(StudentId.From(id), request.Date), cancellationToken), id, reader, cancellationToken);

    /// <summary>A JSON file to hand to the data subject; documents are short-lived links in it (DEC-539).</summary>
    private static async Task<IResult> ExportStudentData(
        Guid id,
        HttpContext httpContext,
        IQueryHandler<GetStudentDataExportQuery, StudentDataExportDto> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetStudentDataExportQuery(StudentId.From(id)), cancellationToken);
        if (result.IsFailure)
        {
            return ProblemResults.Problem(result.Error);
        }

        httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"ogrenci-verisi-{id}.json\"";
        return TypedResults.Ok(result.Value);
    }

    private static async Task<IResult> EraseStudent(
        Guid id,
        EraseStudentRequest request,
        ICommandHandler<EraseStudentCommand, Unit> handler,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken) =>
        await ThenRead(await handler.Handle(new EraseStudentCommand(StudentId.From(id), request.Reason), cancellationToken), id, reader, cancellationToken);

    private static async Task<IResult> ReactivateStudent(
        Guid id,
        ICommandHandler<ReactivateStudentCommand, Unit> handler,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken) =>
        await ThenRead(await handler.Handle(new ReactivateStudentCommand(StudentId.From(id)), cancellationToken), id, reader, cancellationToken);

    private static async Task<IResult> FindGuardiansByPhone(
        string? phone,
        IQueryHandler<FindGuardiansByPhoneQuery, PagedResult<GuardianDto>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new FindGuardiansByPhoneQuery(phone), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(PageResponse<GuardianDto>.From(result.Value)) : ProblemResults.Problem(result.Error);
    }

    private static async Task<IResult> UpdateGuardian(
        Guid id,
        UpdateGuardianRequest request,
        ICommandHandler<UpdateGuardianCommand, Unit> handler,
        IQueryHandler<GetGuardianQuery, GuardianDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new UpdateGuardianCommand(
            GuardianId.From(id), request.FirstName, request.LastName, request.Phone, request.Email, request.Address, request.Version);
        var updated = await handler.Handle(command, cancellationToken);
        return updated.IsSuccess
            ? (await reader.Handle(new GetGuardianQuery(GuardianId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(updated.Error);
    }

    private static async Task<IResult> ThenRead(
        Domain.SharedKernel.Result<Unit> changed,
        Guid id,
        IQueryHandler<GetStudentQuery, StudentDto> reader,
        CancellationToken cancellationToken) =>
        changed.IsSuccess
            ? (await reader.Handle(new GetStudentQuery(StudentId.From(id)), cancellationToken)).OkOrProblem()
            : ProblemResults.Problem(changed.Error);
}
