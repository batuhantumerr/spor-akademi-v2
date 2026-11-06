using Microsoft.EntityFrameworkCore;
using Npgsql;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// Handlers check uniqueness before saving, but two concurrent requests can both pass the check;
/// the database index is the backstop (NFR-REL-08). This maps such violations to the same expected
/// error the check would have returned, instead of a 500.
/// </summary>
internal static class ConstraintErrors
{
    private const string UniqueViolation = "23505";

    private static readonly Dictionary<string, Error> ByConstraint = new(StringComparer.Ordinal)
    {
        ["ix_schools_slug"] = SchoolErrors.SlugTaken,
        ["ix_sports_tenant_id_name_key"] = SportErrors.NameTaken,
        ["ix_facilities_tenant_id_name_key"] = FacilityErrors.NameTaken,
        ["ix_users_email"] = UserErrors.EmailTaken,
        ["ix_guardians_tenant_id_phone"] = GuardianErrors.PhoneTaken,
    };

    public static bool TryMap(DbUpdateException exception, out Error error)
    {
        if (exception.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: { } name }
            && ByConstraint.TryGetValue(name, out var mapped))
        {
            error = mapped;
            return true;
        }

        error = Error.None;
        return false;
    }
}
