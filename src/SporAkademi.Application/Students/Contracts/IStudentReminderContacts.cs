namespace SporAkademi.Application.Students.Contracts;

/// <summary>One address a fee reminder goes to: a guardian's, or an adult student's own (DEC-356).</summary>
public sealed record ReminderRecipient(string FirstName, string LastName, string Email);

/// <summary>An active student and who receives their fee reminders.</summary>
public sealed record ReminderContacts(Guid StudentId, string FirstName, string LastName, IReadOnlyList<ReminderRecipient> Recipients);

/// <summary>
/// Who receives fee reminders (P2-F4, DEC-356), for the Billing module. Only School Admin features use it: guardian
/// data never reaches coach views (DEC-007). Recipients are the student's guardians that have an e-mail address,
/// each once; a student without one gets reminders at their own address only when they are an adult on
/// <c>today</c> (a minor is never e-mailed). Left and anonymized students have no recipients and are omitted.
/// </summary>
public interface IStudentReminderContacts
{
    /// <summary>Active students of the current school among <paramref name="studentIds"/> that have at least one recipient.</summary>
    Task<IReadOnlyDictionary<Guid, ReminderContacts>> GetAsync(IReadOnlyCollection<Guid> studentIds, DateOnly today, CancellationToken cancellationToken);
}
