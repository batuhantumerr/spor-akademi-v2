using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SporAkademi.Application.Abstractions.Auditing;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Audit;

/// <summary>The default for hosts without HTTP requests (the demo seeder): no correlation id.</summary>
internal sealed class NoRequestCorrelation : IRequestCorrelation
{
    public string? CorrelationId => null;
}

/// <summary>
/// Turns a change or an explicit record into an <see cref="AuditEntry"/> with the actor, the time and the correlation id,
/// and keeps the explicit records of the current unit of work until its next save. Scoped: one per request or job scope.
/// </summary>
internal sealed class AuditTrailBuffer(ICurrentUser currentUser, TimeProvider timeProvider, IRequestCorrelation correlation)
{
    private readonly List<AuditRecord> _pending = [];

    public void Add(AuditRecord record) => _pending.Add(record);

    public IReadOnlyList<AuditRecord> TakePending()
    {
        var taken = _pending.ToList();
        _pending.Clear();
        return taken;
    }

    public SchoolId? CurrentSchool => currentUser.TenantId is { } tenant ? SchoolId.From(tenant) : null;

    public AuditEntry Entry(SchoolId school, AuditRecord record) => new(
        school,
        timeProvider.GetUtcNow(),
        currentUser.UserId,
        currentUser.Role?.ToString(),
        record.Action,
        record.EntityType,
        record.EntityId,
        record.Fields,
        record.Reason,
        correlation.CorrelationId);
}

/// <inheritdoc cref="IAuditTrail"/>
internal sealed class AuditTrail(AuditTrailBuffer buffer, AppDbContext db) : IAuditTrail
{
    public void Add(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        buffer.Add(record);
    }

    /// <summary>
    /// A plain <c>INSERT</c>, not <c>SaveChanges</c>: a read must not save whatever else the scope's context happens to
    /// track. Work without a school (Platform Admin) has no school audit log and records nothing.
    /// </summary>
    public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (buffer.CurrentSchool is not { } school)
        {
            return;
        }

        var e = buffer.Entry(school, record);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO audit.audit_entries (id, tenant_id, occurred_at, user_id, user_role, action, entity_type, entity_id, fields, reason, correlation_id)
            VALUES ({e.Id}, {e.TenantId.Value}, {e.OccurredAt}, {e.UserId}, {e.UserRole}, {e.Action.ToString()}, {e.EntityType.ToString()},
                    {e.EntityId}, {e.Fields}, {e.Reason}, {e.CorrelationId})
            """,
            cancellationToken);
    }
}

/// <summary>
/// P4-05 (DEC-534): every save records what changed in personal data (students and their guardian links, guardians,
/// documents, users) as audit entries added to the same save, so they commit or roll back with the change itself. Only
/// the names of changed fields are taken, never their values. Registered before the tenant write guard, which then
/// checks the new entries like any other row.
/// </summary>
internal sealed class PersonalDataAuditInterceptor(AuditTrailBuffer buffer) : SaveChangesInterceptor
{
    /// <summary>Fields of no interest to an auditor: keys, derived values and bookkeeping (DEC-534).</summary>
    private static readonly HashSet<string> IgnoredFields = new(StringComparer.Ordinal)
    {
        "Id",
        nameof(ITenantScoped.TenantId),
        nameof(Student.SearchKey),
        ConcurrencyToken.PropertyName,
        AuditColumns.CreatedAt,
        AuditColumns.CreatedBy,
        AuditColumns.UpdatedAt,
        AuditColumns.UpdatedBy,

        // Login and invitation counters of a user: security bookkeeping written on every attempt, not personal data.
        nameof(User.FailedLoginCount),
        nameof(User.FirstFailedLoginAt),
        nameof(User.LockoutEndsAt),
        nameof(User.PasswordResetRequestedAt),
        nameof(User.InvitationWindowStartedAt),
        nameof(User.InvitationsInWindow),
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changes = new Dictionary<(AuditEntityType Type, Guid Id), Change>();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Entity is StudentGuardian link)
            {
                // A guardian link is part of the student: adding, removing or changing one changes "Guardians".
                Merge(changes, AuditEntityType.Student, link.StudentId.Value, link.TenantId, AuditAction.Update, ["Guardians"]);
                continue;
            }

            if (Describe(entry.Entity) is not { } subject)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    Merge(changes, subject.Type, subject.Id, subject.School, AuditAction.Create, FieldsWithValues(entry));
                    break;
                case EntityState.Deleted:
                    Merge(changes, subject.Type, subject.Id, subject.School, AuditAction.Delete, []);
                    break;
                default:
                    var modified = ModifiedFields(entry);
                    if (modified.Count > 0)
                    {
                        var action = subject.Type == AuditEntityType.Student && modified.Contains(nameof(Student.AnonymizedAt))
                            ? AuditAction.Anonymize
                            : AuditAction.Update;
                        Merge(changes, subject.Type, subject.Id, subject.School, action, modified);
                    }

                    break;
            }
        }

        foreach (var record in buffer.TakePending())
        {
            var key = (record.EntityType, record.EntityId ?? Guid.Empty);
            if (record.EntityId is not null && changes.TryGetValue(key, out var change))
            {
                // The handler's description wins; the fields it changed are added to it.
                change.Action = record.Action;
                change.Reason = record.Reason;
                change.Fields.UnionWith(record.Fields);
            }
            else if (buffer.CurrentSchool is { } school)
            {
                context.Add(buffer.Entry(school, record));
            }
        }

        foreach (var ((type, id), change) in changes)
        {
            context.Add(buffer.Entry(change.School, new AuditRecord(change.Action, type, id, change.Fields, change.Reason)));
        }
    }

    private static (AuditEntityType Type, Guid Id, SchoolId School)? Describe(object entity) => entity switch
    {
        Student s => (AuditEntityType.Student, s.Id.Value, s.TenantId),
        Guardian g => (AuditEntityType.Guardian, g.Id.Value, g.TenantId),
        StudentDocument d => (AuditEntityType.StudentDocument, d.Id.Value, d.TenantId),
        // A Platform Admin has no school and so no school audit log.
        User { TenantId: { } school } u => (AuditEntityType.User, u.Id.Value, school),
        _ => null,
    };

    private static void Merge(
        Dictionary<(AuditEntityType Type, Guid Id), Change> changes,
        AuditEntityType type,
        Guid id,
        SchoolId school,
        AuditAction action,
        IEnumerable<string> fields)
    {
        if (!changes.TryGetValue((type, id), out var change))
        {
            change = new Change(school, action);
            changes[(type, id)] = change;
        }
        else if (change.Action == AuditAction.Update)
        {
            // Create, Delete and Anonymize say more than the guardian links' Update.
            change.Action = action;
        }

        change.Fields.UnionWith(fields);
    }

    private static List<string> FieldsWithValues(EntityEntry entry) =>
    [
        .. entry.Properties.Where(p => !IgnoredFields.Contains(p.Metadata.Name) && !p.Metadata.IsShadowProperty() && p.CurrentValue is not null)
            .Select(p => p.Metadata.Name),
        .. entry.ComplexProperties.Where(p => p.CurrentValue is not null).Select(p => p.Metadata.Name),
    ];

    private static List<string> ModifiedFields(EntityEntry entry) =>
    [
        .. entry.Properties.Where(p => Changed(p) && !IgnoredFields.Contains(p.Metadata.Name)).Select(p => p.Metadata.Name),
        .. entry.ComplexProperties.Where(Changed).Select(p => p.Metadata.Name),
    ];

    /// <summary>
    /// A changed value, not only a property marked modified: a version-checked save marks the whole row modified
    /// (<c>ExpectVersion</c>) although only some values changed.
    /// </summary>
    private static bool Changed(PropertyEntry property) =>
        property.IsModified && !property.Metadata.GetValueComparer().Equals(property.OriginalValue, property.CurrentValue);

    private static bool Changed(ComplexPropertyEntry property) =>
        property.Properties.Any(Changed) || property.ComplexProperties.Any(Changed);

    private sealed class Change(SchoolId school, AuditAction action)
    {
        public SchoolId School { get; } = school;

        public AuditAction Action { get; set; } = action;

        public string? Reason { get; set; }

        public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);
    }
}
