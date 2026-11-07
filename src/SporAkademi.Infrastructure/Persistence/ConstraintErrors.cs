using Microsoft.EntityFrameworkCore;
using Npgsql;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Attendance;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// Handlers check uniqueness and usage before saving, but two concurrent requests can both pass the
/// check, and some rules are only enforced by the database; the constraints are the backstop
/// (NFR-REL-08). This maps such violations to the same expected error the check would have returned,
/// instead of a 500 (DEC-133).
/// </summary>
internal static class ConstraintErrors
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    /// <summary>A delete blocked by an <c>ON DELETE RESTRICT</c> foreign key.</summary>
    private const string RestrictViolation = "23001";

    private static readonly Dictionary<string, Error> UniqueByConstraint = new(StringComparer.Ordinal)
    {
        ["ix_schools_slug"] = SchoolErrors.SlugTaken,
        ["ix_sports_tenant_id_name_key"] = SportErrors.NameTaken,
        ["ix_facilities_tenant_id_name_key"] = FacilityErrors.NameTaken,
        ["ix_users_email"] = UserErrors.EmailTaken,
        ["ix_groups_tenant_id_name_key"] = GroupErrors.NameTaken,
        ["ix_group_fees_group_id_effective_from"] = FeeErrors.AlreadyDefined,
        // DEC-050: the partial unique index behind "already enrolled".
        ["ix_enrollments_active_student_group"] = EnrollmentErrors.AlreadyEnrolled,
        ["ix_guardians_tenant_id_phone"] = GuardianErrors.PhoneTaken,
        // DEC-173: a concurrent first save of the same session or student record; the caller retries.
        [SessionConfiguration.UniqueKeyIndex] = PersistenceErrors.ConcurrencyConflict,
        [AttendanceRecordConfiguration.PrimaryKey] = PersistenceErrors.ConcurrencyConflict,
        // Two photos uploaded for one student at the same time: the later one retries (DEC-325).
        [Students.StudentDocumentConfiguration.OnePhotoIndex] = PersistenceErrors.ConcurrencyConflict,
    };

    /// <summary>
    /// Deleting a row that is still referenced (restrict or foreign key violation). Only mapped when the save deletes a row of the referenced
    /// type: the same constraint failing on an insert (an unknown or other school's id) is a programmer
    /// error or an attack and stays a 500.
    /// </summary>
    private static readonly Dictionary<string, (Type Deleted, Error Error)> InUseByConstraint = new(StringComparer.Ordinal)
    {
        // A facility used only by archived groups passes the handler's check (US-07) but not the key.
        ["fk_groups_facility_tenant_id_facility_id"] = (typeof(Facility), FacilityErrors.InUse),
        ["fk_groups_sport_tenant_id_sport_id"] = (typeof(Sport), SportErrors.InUse),
    };

    public static bool TryMap(DbUpdateException exception, out Error error)
    {
        switch (exception.InnerException)
        {
            case PostgresException { SqlState: UniqueViolation, ConstraintName: { } name }
                when UniqueByConstraint.TryGetValue(name, out var taken):
                error = taken;
                return true;

            case PostgresException { SqlState: ForeignKeyViolation or RestrictViolation, ConstraintName: { } name }
                when InUseByConstraint.TryGetValue(name, out var inUse)
                    && exception.Entries.Any(e => e.State == EntityState.Deleted && e.Metadata.ClrType == inUse.Deleted):
                error = inUse.Error;
                return true;

            default:
                error = Error.None;
                return false;
        }
    }
}
