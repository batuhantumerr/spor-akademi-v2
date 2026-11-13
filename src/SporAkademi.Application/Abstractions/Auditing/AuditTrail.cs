namespace SporAkademi.Application.Abstractions.Auditing;

/// <summary>What was done to personal data (P4-05, DEC-530).</summary>
public enum AuditAction
{
    /// <summary>One record was shown or one document was opened.</summary>
    Read,

    /// <summary>A list or search query: one entry for the query, not one per row (DEC-532).</summary>
    List,

    Create,
    Update,
    Delete,

    /// <summary>The retention job removed the personal data of a student who left (DEC-008, DEC-363).</summary>
    Anonymize,

    /// <summary>A machine-readable copy was handed out (P4-06, NFR-PRV-05).</summary>
    Export,

    /// <summary>The personal data was erased on the data subject's request (P4-06, NFR-PRV-05).</summary>
    Erase,
}

/// <summary>The kinds of records whose personal data is audited (P4-05, DEC-531).</summary>
public enum AuditEntityType
{
    Student,
    Guardian,

    /// <summary>A health report or photo (P2-F6).</summary>
    StudentDocument,

    User,
}

/// <summary>
/// One audit entry before it is stored. <paramref name="Fields"/> holds field (or query parameter) names, never values;
/// <paramref name="Reason"/> is a fixed code (e.g. an erasure reason), never free text (DEC-533).
/// </summary>
public sealed record AuditRecord(
    AuditAction Action,
    AuditEntityType EntityType,
    Guid? EntityId,
    IReadOnlyCollection<string> Fields,
    string? Reason = null);

/// <summary>
/// Records access to personal data (P4-05). Who (user id and role), when (<see cref="TimeProvider"/>), the school and
/// the correlation id are added by the implementation. Changes made through EF are recorded on their own (DEC-534);
/// this is for reads and for entries a handler wants to describe itself.
/// </summary>
public interface IAuditTrail
{
    /// <summary>
    /// Adds an entry to the current unit of work: it is saved with the next <c>SaveChanges</c>, in the same transaction
    /// as the change it describes. A change of the same record in that save is merged into it (its field names are
    /// added; this action wins).
    /// </summary>
    void Add(AuditRecord record);

    /// <summary>Stores an entry right away, outside any transaction: for reads, which have none.</summary>
    Task RecordAsync(AuditRecord record, CancellationToken cancellationToken);
}

/// <summary>The correlation id of the current request (NFR-OBS-01); <c>null</c> for background work.</summary>
public interface IRequestCorrelation
{
    string? CorrelationId { get; }
}
