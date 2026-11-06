using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "enrollments");

            migrationBuilder.CreateTable(
                name: "enrollments",
                schema: "enrollments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollments", x => x.id);
                    table.UniqueConstraint("ak_enrollments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_enrollments_end_date", "(status = 'Ended') = (end_date IS NOT NULL) AND (end_date IS NULL OR end_date >= start_date)");
                    table.ForeignKey(
                        name: "fk_enrollments_group_tenant_id_group_id",
                        columns: x => new { x.tenant_id, x.group_id },
                        principalSchema: "groups",
                        principalTable: "groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_enrollments_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_enrollments_student_tenant_id_student_id",
                        columns: x => new { x.tenant_id, x.student_id },
                        principalSchema: "students",
                        principalTable: "students",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_active_student_group",
                schema: "enrollments",
                table: "enrollments",
                columns: new[] { "tenant_id", "student_id", "group_id" },
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_enrollments_tenant_id_group_id_status",
                schema: "enrollments",
                table: "enrollments",
                columns: new[] { "tenant_id", "group_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enrollments",
                schema: "enrollments");
        }
    }
}
