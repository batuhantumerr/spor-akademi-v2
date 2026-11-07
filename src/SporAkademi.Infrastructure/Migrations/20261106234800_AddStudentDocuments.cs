using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "student_documents",
                schema: "students",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student_documents", x => x.id);
                    table.CheckConstraint("ck_student_documents_size_bytes", "size_bytes > 0");
                    table.ForeignKey(
                        name: "fk_student_documents_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_documents_students_tenant_id_student_id",
                        columns: x => new { x.tenant_id, x.student_id },
                        principalSchema: "students",
                        principalTable: "students",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_student_documents_one_photo",
                schema: "students",
                table: "student_documents",
                columns: new[] { "tenant_id", "student_id" },
                unique: true,
                filter: "type = 'Photo'");

            migrationBuilder.CreateIndex(
                name: "ix_student_documents_storage_key",
                schema: "students",
                table: "student_documents",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_documents_tenant_id_student_id_type",
                schema: "students",
                table: "student_documents",
                columns: new[] { "tenant_id", "student_id", "type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "student_documents",
                schema: "students");
        }
    }
}
