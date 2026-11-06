using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

/// <summary>Students of the current school for other modules (tenant filter applies). Never touches guardians.</summary>
internal sealed class StudentDirectory(AppDbContext db) : IStudentDirectory
{
    public async Task<StudentInfo?> FindAsync(StudentId studentId, CancellationToken cancellationToken) =>
        (await GetManyAsync([studentId.Value], cancellationToken)).GetValueOrDefault(studentId.Value);

    public async Task<IReadOnlyDictionary<Guid, StudentInfo>> GetManyAsync(IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studentIds);
        if (studentIds.Count == 0)
        {
            return new Dictionary<Guid, StudentInfo>();
        }

        var ids = studentIds.Distinct().Select(StudentId.From).ToList();
        var rows = await db.Set<Student>().AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.FirstName, s.LastName, s.BirthDate, s.Status, s.EmergencyContact.Phone })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            r => r.Id.Value,
            r => new StudentInfo(r.Id.Value, r.FirstName, r.LastName, r.BirthDate, r.Status, r.Phone.Value));
    }
}
