using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// P4-04 (DEC-522): every migration starts with <c>SET app.maintenance = 'on'</c>, so a data migration run by the
/// migrator (the tables' owner, bound by <c>FORCE ROW LEVEL SECURITY</c>) sees and changes every school's rows instead
/// of silently affecting none. One place for every way migrations run: <c>scripts/ef.sh</c>, the migrator image's
/// bundle, the idempotent SQL script, CI and the test fixture. The switch opens nothing for the application role (the
/// maintenance policy belongs to the owner), and the API never migrates.
/// </summary>
#pragma warning disable EF1001 // Npgsql's generator takes its internal options; the subclass only prepends one statement.
internal sealed class MaintenanceMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : NpgsqlMigrationsSqlGenerator(dependencies, npgsqlSingletonOptions)
#pragma warning restore EF1001
{
    /// <summary>Session level (not <c>SET LOCAL</c>), so it also holds for operations that suppress the transaction.</summary>
    public const string MaintenanceSql = "SET app.maintenance = 'on';";

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations.Count == 0
            ? base.Generate(operations, model, options)
            : base.Generate([new SqlOperation { Sql = MaintenanceSql }, .. operations], model, options);
    }
}
