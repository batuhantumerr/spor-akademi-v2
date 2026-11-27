using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <summary>
    /// V1-03 (DEC-689): an audit entry may belong to no school (a Platform Admin resetting another Platform Admin's MFA). The
    /// platform-table policy of DEC-535 already needs no change: a school's scope never matches a null <c>tenant_id</c> and
    /// work without a school writes and reads all rows.
    /// </summary>
    public partial class AuditEntryWithoutSchool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                schema: "audit",
                table: "audit_entries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Entries of no school cannot be kept in a column that requires one. The owner's maintenance switch lets the
            // migrator touch the rows it owns (DEC-522).
            migrationBuilder.Sql("SELECT set_config('app.maintenance', 'on', true); DELETE FROM audit.audit_entries WHERE tenant_id IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                schema: "audit",
                table: "audit_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
