using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Students.Contracts;

/// <summary>
/// Integration message after a student's leave is committed (US-15). Ids only (NFR-PRV-03); a future notification
/// handler (P2-F4) reads names and addresses when it runs, so anonymized students are never e-mailed from stale data.
/// </summary>
[OutboxMessage("students.student-left.v1")]
public sealed record StudentLeftMessage(Guid StudentId, DateOnly LeftOn) : IOutboxMessage;
