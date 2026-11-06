using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Attendance;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;

namespace SporAkademi.Api.Endpoints.Attendance;

internal sealed record SaveAttendanceRequest(IReadOnlyList<AttendanceMarkInput>? Records);

/// <summary>
/// Attendance (appendix D §2.9, §2.7; US-19, US-20, US-21). School Admin for every group, a coach for
/// own active groups only. A session is addressed by <c>{sessionKey}</c> = <c>YYYY-MM-DDTHH:mm</c> (DEC-016).
/// </summary>
internal static class AttendanceEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        var attendance = app.MapGroup("/api/v1").WithTags("Attendance").RequireRole(Role.SchoolAdmin, Role.Coach);
        attendance.MapGet("/groups/{id:guid}/sessions/{sessionKey}/attendance", GetSessionAttendance)
            .Produces<SessionAttendanceDto>().ProducesProblems(400, 404);
        attendance.MapPut("/groups/{id:guid}/sessions/{sessionKey}/attendance", SaveAttendance)
            .Produces<SessionAttendanceDto>().ProducesProblems(400, 403, 404, 409, 422);
        attendance.MapGet("/groups/{id:guid}/attendance", GetGroupMonth)
            .Produces<GroupAttendanceMonthDto>().ProducesProblems(400, 404);
        attendance.MapGet("/students/{id:guid}/attendance", GetStudentAttendance)
            .Produces<StudentAttendanceDto>().ProducesProblems(400, 404);
        return app;
    }

    private static async Task<IResult> GetSessionAttendance(
        Guid id,
        string sessionKey,
        IQueryHandler<GetSessionAttendanceQuery, SessionAttendanceDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetSessionAttendanceQuery(GroupId.From(id), sessionKey), cancellationToken)).OkOrProblem();

    private static async Task<IResult> SaveAttendance(
        Guid id,
        string sessionKey,
        SaveAttendanceRequest request,
        ICommandHandler<SaveAttendanceCommand, Unit> handler,
        IQueryHandler<GetSessionAttendanceQuery, SessionAttendanceDto> reader,
        CancellationToken cancellationToken)
    {
        var command = new SaveAttendanceCommand(GroupId.From(id), sessionKey, request.Records);
        var saved = await AttendanceConflicts.RetryAsync(() => handler.Handle(command, cancellationToken));
        return saved.IsSuccess
            ? (await reader.Handle(new GetSessionAttendanceQuery(GroupId.From(id), sessionKey), cancellationToken)).OkOrProblem()
            : Errors.ProblemResults.Problem(saved.Error);
    }

    private static async Task<IResult> GetGroupMonth(
        Guid id,
        string? month,
        IQueryHandler<GetGroupAttendanceMonthQuery, GroupAttendanceMonthDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetGroupAttendanceMonthQuery(GroupId.From(id), month), cancellationToken)).OkOrProblem();

    private static async Task<IResult> GetStudentAttendance(
        Guid id,
        DateOnly? from,
        DateOnly? to,
        IQueryHandler<GetStudentAttendanceQuery, StudentAttendanceDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetStudentAttendanceQuery(StudentId.From(id), from, to), cancellationToken)).OkOrProblem();
}
