using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Students.Contracts;

/// <summary>
/// Integration message after a student's leave is committed (US-15). Ids only (NFR-PRV-03); a future notification
/// handler (P2-F4) reads names and addresses when it runs, so anonymized students are never e-mailed from stale data.
/// </summary>
[OutboxMessage("students.student-left.v1")]
public sealed record StudentLeftMessage(Guid StudentId, DateOnly LeftOn) : IOutboxMessage;

/// <summary>
/// The metadata of a student document is gone (deleted, or a photo replaced); its stored object must be deleted after
/// commit (P2-F6, DEC-325). Carries only the generated storage key, never personal data; the school is the message's tenant.
/// </summary>
[OutboxMessage("documents.object-deleted.v1")]
public sealed record DocumentObjectDeletedMessage(string StorageKey) : IOutboxMessage;
