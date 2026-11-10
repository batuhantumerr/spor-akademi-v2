using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students.Contracts;

/// <summary>
/// Sibling sets for the fee rules (P2-F8, DEC-371), for the Billing module: students of the current school are siblings
/// when they are linked to a common guardian, transitively (connected components over the student–guardian links). A
/// student without guardians (e.g. an adult) has no siblings. Only ids leave the module; guardian data never does
/// (DEC-007). The sets follow today's links: there is no link history (DEC-374).
/// </summary>
public interface IStudentSiblings
{
    /// <summary>
    /// For each of <paramref name="studentIds"/> that has at least one sibling: the whole sibling set, the student
    /// included, ordered by id. Its first id is a stable key of the family. Students without siblings are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<StudentId, IReadOnlyList<StudentId>>> GetSiblingSetsAsync(IReadOnlyCollection<StudentId> studentIds, CancellationToken cancellationToken);

    /// <summary>
    /// The sibling set of one student (the student included, ordered by id, so its first id is the same family key as in
    /// <see cref="GetSiblingSetsAsync"/>); just the student when they have no siblings. Reads only that family's links,
    /// not the whole school's (a single due: payments, reminders; DEC-373).
    /// </summary>
    Task<IReadOnlyList<StudentId>> GetFamilyAsync(StudentId studentId, CancellationToken cancellationToken);
}
