using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Students;

/// <summary>
/// Sibling sets (DEC-371) of the current school's students (tenant filter applies). One query reads every
/// student–guardian link of the school (two ids per row; a school has at most a few thousand) and the connected
/// components are found in memory, so a transitive family (A and B share one guardian, B and C another) is one set
/// however many students are asked for.
/// </summary>
internal sealed class StudentSiblings(AppDbContext db, ICurrentUser currentUser) : IStudentSiblings
{
    /// <summary>
    /// One recursive query over the student–guardian links, from the student through their guardians to the guardians'
    /// other students and so on (<c>UNION</c> drops repeats, so cycles end). Raw SQL bypasses the tenant filter, so the
    /// school is part of every join (section 5.4); without a school only the student comes back.
    /// </summary>
    public async Task<IReadOnlyList<StudentId>> GetFamilyAsync(StudentId studentId, CancellationToken cancellationToken)
    {
        if (currentUser.TenantId is not { } tenant)
        {
            return [studentId];
        }

        var start = studentId.Value;
        var ids = await db.Database.SqlQuery<Guid>(
                $"""
                WITH RECURSIVE family(student_id) AS (
                    SELECT {start}::uuid
                    UNION
                    SELECT other.student_id
                    FROM family f
                    JOIN students.student_guardians own ON own.student_id = f.student_id AND own.tenant_id = {tenant}
                    JOIN students.student_guardians other ON other.guardian_id = own.guardian_id AND other.tenant_id = {tenant}
                )
                SELECT student_id AS "Value" FROM family
                """)
            .ToListAsync(cancellationToken);
        return [.. ids.Select(StudentId.From).OrderBy(s => s.Value)];
    }

    public async Task<IReadOnlyDictionary<StudentId, IReadOnlyList<StudentId>>> GetSiblingSetsAsync(
        IReadOnlyCollection<StudentId> studentIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studentIds);
        var result = new Dictionary<StudentId, IReadOnlyList<StudentId>>();
        if (studentIds.Count == 0)
        {
            return result;
        }

        var links = await db.Set<StudentGuardian>().AsNoTracking()
            .Select(l => new { l.StudentId, l.GuardianId })
            .ToListAsync(cancellationToken);

        var parent = new Dictionary<StudentId, StudentId>();
        foreach (var guardian in links.GroupBy(l => l.GuardianId))
        {
            var first = guardian.First().StudentId;
            foreach (var link in guardian)
            {
                Union(parent, first, link.StudentId);
            }
        }

        // Find compresses paths (it writes to the dictionary), so the keys are copied first.
        var families = parent.Keys.ToList()
            .GroupBy(student => Find(parent, student))
            .Where(family => family.Count() > 1)
            .Select(family => (IReadOnlyList<StudentId>)[.. family.OrderBy(s => s.Value)]);
        var wanted = studentIds.ToHashSet();
        foreach (var family in families)
        {
            foreach (var student in family.Where(wanted.Contains))
            {
                result[student] = family;
            }
        }

        return result;
    }

    private static StudentId Find(Dictionary<StudentId, StudentId> parent, StudentId student)
    {
        if (!parent.TryGetValue(student, out var root))
        {
            parent[student] = student;
            return student;
        }

        while (root != parent[root])
        {
            root = parent[root];
        }

        // Path compression keeps later lookups short.
        while (student != root)
        {
            var next = parent[student];
            parent[student] = root;
            student = next;
        }

        return root;
    }

    private static void Union(Dictionary<StudentId, StudentId> parent, StudentId a, StudentId b)
    {
        var (rootA, rootB) = (Find(parent, a), Find(parent, b));
        if (rootA != rootB)
        {
            parent[rootB] = rootA;
        }
    }
}
