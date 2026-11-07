using Microsoft.EntityFrameworkCore.Migrations;
using SporAkademi.Infrastructure.Jobs;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <summary>
    /// P2-F2 (DEC-331): Hangfire's storage schema, created by the migrator from the install scripts shipped in
    /// Hangfire.PostgreSql (versions 3 to 23). The version range is fixed here; a package upgrade adds a migration.
    /// </summary>
    public partial class AddHangfireSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(HangfireSchema.InstallScript(fromVersion: 3, toVersion: 23));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DROP SCHEMA IF EXISTS \"{HangfireSchema.Name}\" CASCADE;");
        }
    }
}
