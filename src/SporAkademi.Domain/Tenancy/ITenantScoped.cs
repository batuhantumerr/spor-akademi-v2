namespace SporAkademi.Domain.Tenancy;

/// <summary>
/// Marks an aggregate that belongs to exactly one school. Reads are filtered and writes guarded by
/// the current tenant automatically (ADR-0003). The tenant is set at creation and never changes.
/// </summary>
public interface ITenantScoped
{
    SchoolId TenantId { get; }
}
