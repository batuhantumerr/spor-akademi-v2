namespace SporAkademi.Infrastructure.Persistence;

/// <summary>One PostgreSQL schema per module (ADR-0001).</summary>
internal static class Schemas
{
    public const string Tenancy = "tenancy";
    public const string Identity = "identity";
    public const string Organization = "organization";
    public const string Groups = "groups";
    public const string Students = "students";
    public const string Enrollments = "enrollments";
    public const string Attendance = "attendance";
    public const string Billing = "billing";
    public const string Announcements = "announcements";

    /// <summary>Cross-cutting: the personal data audit log (P4-05).</summary>
    public const string Audit = "audit";

    /// <summary>Platform infrastructure, not a module: the transactional outbox (P2-F3).</summary>
    public const string Outbox = "outbox";
}
