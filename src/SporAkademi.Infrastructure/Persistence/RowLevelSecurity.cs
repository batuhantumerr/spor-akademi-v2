namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// The SQL of the row level security policies (P4-04, DEC-520 – DEC-523), for migrations. A migration that creates a
/// table with a <c>tenant_id</c> column calls <see cref="ForTenantTable"/> for it; <c>RowLevelSecurityTests</c> fails
/// for any tenant table without the policy.
/// </summary>
/// <remarks>
/// The policies read the session settings written by <see cref="TenantSessionInterceptor"/> through the functions
/// created in the <c>AddRowLevelSecurity</c> migration. <c>tenancy.current_tenant_id()</c> is <c>STABLE</c>, so the
/// planner treats it as a constant: <c>tenant_id = tenancy.current_tenant_id()</c> is an index condition on the
/// tenant-leading indexes every tenant table already has (ADR-0003), not a per-row filter.
/// </remarks>
public static class RowLevelSecurity
{
    /// <summary>
    /// Enables and forces row level security on a school table: only the current school's rows are visible, and only
    /// rows of the current school can be written. The table owner (the migrator) gets the maintenance policy only.
    /// </summary>
    public static string ForTenantTable(string schema, string table) =>
        Policies(schema, table, "tenant_id = tenancy.current_tenant_id()");

    /// <summary>
    /// A platform table (DEC-105, DEC-302): a school sees and writes its own rows; work without a school (login,
    /// Platform Admin, platform jobs) sees all of them. <paramref name="tenantColumn"/> names the school column.
    /// </summary>
    public static string ForPlatformTable(string schema, string table, string tenantColumn = "tenant_id") =>
        Policies(schema, table, $"{tenantColumn} = tenancy.current_tenant_id() OR tenancy.is_platform_scope()");

    /// <summary>
    /// A table without a school column whose rows belong to a row of <paramref name="parentTable"/> (DEC-527): a row is
    /// visible and writable only while its parent row is, under the parent's own policy. <paramref name="foreignKey"/>
    /// is the column holding the parent's <c>id</c> (its primary key, so the check is one index lookup).
    /// </summary>
    public static string ForOwnedTable(string schema, string table, string parentTable, string foreignKey) =>
        Policies(schema, table, $"EXISTS (SELECT 1 FROM {parentTable} parent WHERE parent.id = {table}.{foreignKey})");

    /// <summary>
    /// P4-10 (DEC-581): on a platform table that only work without a school may change (the feature flag values the
    /// Platform Admin sets), restrictive policies refuse INSERT, UPDATE and DELETE in a school's scope, so a school user
    /// can still read its own rows but never write them, even if the application tried. The owner's maintenance switch
    /// stays open for data migrations. Restrictive policies are ANDed with <c>tenant_isolation</c>.
    /// </summary>
    public static string PlatformWritesOnly(string schema, string table)
    {
        const string condition = "tenancy.is_platform_scope() OR tenancy.is_maintenance()";
        return $"""
            CREATE POLICY platform_insert ON {schema}.{table} AS RESTRICTIVE FOR INSERT WITH CHECK ({condition});
            CREATE POLICY platform_update ON {schema}.{table} AS RESTRICTIVE FOR UPDATE USING ({condition}) WITH CHECK ({condition});
            CREATE POLICY platform_delete ON {schema}.{table} AS RESTRICTIVE FOR DELETE USING ({condition});
            """;
    }

    /// <summary>Reverts <see cref="PlatformWritesOnly"/>.</summary>
    public static string DropPlatformWritesOnly(string schema, string table) =>
        $"""
        DROP POLICY IF EXISTS platform_insert ON {schema}.{table};
        DROP POLICY IF EXISTS platform_update ON {schema}.{table};
        DROP POLICY IF EXISTS platform_delete ON {schema}.{table};
        """;

    /// <summary>Reverts <see cref="ForTenantTable"/>, <see cref="ForPlatformTable"/> or <see cref="ForOwnedTable"/>.</summary>
    public static string Drop(string schema, string table) =>
        $"""
        DROP POLICY IF EXISTS maintenance ON {schema}.{table};
        DROP POLICY IF EXISTS tenant_isolation ON {schema}.{table};
        ALTER TABLE {schema}.{table} NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE {schema}.{table} DISABLE ROW LEVEL SECURITY;
        """;

    private static string Policies(string schema, string table, string condition) =>
        $"""
        ALTER TABLE {schema}.{table} ENABLE ROW LEVEL SECURITY;
        ALTER TABLE {schema}.{table} FORCE ROW LEVEL SECURITY;
        CREATE POLICY tenant_isolation ON {schema}.{table} USING ({condition}) WITH CHECK ({condition});
        CREATE POLICY maintenance ON {schema}.{table} TO CURRENT_USER USING (tenancy.is_maintenance()) WITH CHECK (tenancy.is_maintenance());
        """;
}
