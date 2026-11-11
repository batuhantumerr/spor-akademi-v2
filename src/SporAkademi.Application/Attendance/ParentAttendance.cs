using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Attendance;

/// <summary>
/// P3-03: the attendance history of one of the caller's children (DEC-384), the same rows and summary the School Admin
/// sees. Without dates it covers the last <see cref="DefaultDays"/> days up to today in the school's time zone; at
/// most a year per request.
/// </summary>
public sealed record GetChildAttendanceQuery(StudentId StudentId, DateOnly? From, DateOnly? To) : IQuery<StudentAttendanceDto>
{
    public const int DefaultDays = 30;
}

internal sealed class GetChildAttendanceHandler(
    IParentChildren children,
    IQueryHandler<GetStudentAttendanceQuery, StudentAttendanceDto> history,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<GetChildAttendanceQuery, StudentAttendanceDto>
{
    public async Task<Result<StudentAttendanceDto>> Handle(GetChildAttendanceQuery query, CancellationToken cancellationToken)
    {
        if (!await children.IsMineAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<StudentAttendanceDto>(StudentErrors.NotFound);
        }

        var (from, to) = (query.From, query.To);
        if (from is null && to is null)
        {
            to = await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
            from = to.Value.AddDays(1 - GetChildAttendanceQuery.DefaultDays);
        }

        return await history.Handle(new GetStudentAttendanceQuery(query.StudentId, from, to), cancellationToken);
    }
}
