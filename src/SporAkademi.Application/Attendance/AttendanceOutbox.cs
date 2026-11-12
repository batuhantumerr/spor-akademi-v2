using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Attendance.Contracts;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Attendance;

/// <summary>P3-08: each student whose status a save set or changed becomes one integration message (DEC-396, DEC-397).</summary>
internal sealed class PublishAttendanceRecorded(IOutbox outbox) : IDomainEventConsumer<AttendanceRecorded>
{
    public Task<Result> Handle(AttendanceRecorded domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        foreach (var mark in domainEvent.Marks)
        {
            outbox.Enqueue(
                new AttendanceRecordedMessage(domainEvent.SessionId.Value, domainEvent.GroupId.Value, domainEvent.Date, mark.StudentId.Value, mark.Status),
                domainEvent.TenantId.Value);
        }

        return Task.FromResult(Result.Success());
    }
}
