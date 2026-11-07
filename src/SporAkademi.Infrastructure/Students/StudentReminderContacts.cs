using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

/// <summary>Fee reminder recipients (DEC-356) of the current school's students (tenant filters apply).</summary>
internal sealed class StudentReminderContacts(AppDbContext db) : IStudentReminderContacts
{
    public async Task<IReadOnlyDictionary<Guid, ReminderContacts>> GetAsync(IReadOnlyCollection<Guid> studentIds, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studentIds);
        if (studentIds.Count == 0)
        {
            return new Dictionary<Guid, ReminderContacts>();
        }

        var ids = studentIds.Distinct().Select(StudentId.From).ToList();
        var students = await db.Set<Student>().AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.Status == StudentStatus.Active && s.AnonymizedAt == null)
            .Select(s => new { s.Id, s.FirstName, s.LastName, s.BirthDate, s.Email })
            .ToListAsync(cancellationToken);
        var activeIds = students.Select(s => s.Id).ToList();
        var guardians = await (
                from link in db.Set<StudentGuardian>().AsNoTracking()
                join guardian in db.Set<Guardian>().AsNoTracking() on link.GuardianId equals guardian.Id
                where activeIds.Contains(link.StudentId) && guardian.Email != null
                orderby guardian.LastName, guardian.FirstName, guardian.Id
                select new { link.StudentId, guardian.FirstName, guardian.LastName, guardian.Email })
            .ToListAsync(cancellationToken);
        var guardiansOf = guardians.ToLookup(g => g.StudentId);

        var contacts = new Dictionary<Guid, ReminderContacts>();
        foreach (var student in students)
        {
            List<ReminderRecipient> recipients =
            [
                .. guardiansOf[student.Id]
                    .DistinctBy(g => g.Email!.Value)
                    .Select(g => new ReminderRecipient(g.FirstName, g.LastName, g.Email!.Value)),
            ];
            if (recipients.Count == 0 && student.Email is { } own && !Student.IsMinor(student.BirthDate, today))
            {
                recipients.Add(new ReminderRecipient(student.FirstName, student.LastName, own.Value));
            }

            if (recipients.Count > 0)
            {
                contacts[student.Id.Value] = new ReminderContacts(student.Id.Value, student.FirstName, student.LastName, recipients);
            }
        }

        return contacts;
    }
}
