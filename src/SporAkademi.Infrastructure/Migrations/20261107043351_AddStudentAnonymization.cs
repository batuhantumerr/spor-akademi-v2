using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentAnonymization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "emergency_contact_relationship",
                schema: "students",
                table: "students",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "emergency_contact_phone",
                schema: "students",
                table: "students",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(13)",
                oldMaxLength: 13);

            migrationBuilder.AlterColumn<string>(
                name: "emergency_contact_name",
                schema: "students",
                table: "students",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "birth_date",
                schema: "students",
                table: "students",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.CreateTable(
                name: "anonymization_runs",
                schema: "students",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    cutoff = table.Column<DateOnly>(type: "date", nullable: false),
                    personal_data_months = table.Column<int>(type: "integer", nullable: false),
                    anonymized_count = table.Column<int>(type: "integer", nullable: false),
                    skipped_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_anonymization_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_anonymization_runs_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_students_tenant_id_left_at_not_anonymized",
                schema: "students",
                table: "students",
                columns: new[] { "tenant_id", "left_at" },
                filter: "status = 'Left' AND anonymized_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_students_anonymized_left",
                schema: "students",
                table: "students",
                sql: "anonymized_at IS NULL OR status = 'Left'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_students_personal_data",
                schema: "students",
                table: "students",
                sql: "anonymized_at IS NOT NULL OR (birth_date IS NOT NULL AND emergency_contact_name IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_anonymization_runs_tenant_id_started_at",
                schema: "students",
                table: "anonymization_runs",
                columns: new[] { "tenant_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy and one-way in practice: the run history is dropped, and once a student was anonymized the
            // NOT NULL columns below can only be restored with the empty default (''), or the ALTER fails on a NULL
            // birth date. Anonymized personal data never comes back (DEC-363).
            migrationBuilder.DropTable(
                name: "anonymization_runs",
                schema: "students");

            migrationBuilder.DropIndex(
                name: "ix_students_tenant_id_left_at_not_anonymized",
                schema: "students",
                table: "students");

            migrationBuilder.DropCheckConstraint(
                name: "ck_students_anonymized_left",
                schema: "students",
                table: "students");

            migrationBuilder.DropCheckConstraint(
                name: "ck_students_personal_data",
                schema: "students",
                table: "students");

            migrationBuilder.AlterColumn<string>(
                name: "emergency_contact_relationship",
                schema: "students",
                table: "students",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "emergency_contact_phone",
                schema: "students",
                table: "students",
                type: "character varying(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(13)",
                oldMaxLength: 13,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "emergency_contact_name",
                schema: "students",
                table: "students",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "birth_date",
                schema: "students",
                table: "students",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
