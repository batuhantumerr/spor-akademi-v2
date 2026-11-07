using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_reset_requested_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dues_reminders",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recipient_count = table.Column<int>(type: "integer", nullable: false),
                    skip_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dues_reminders", x => x.id);
                    table.CheckConstraint("ck_dues_reminders_sent", "(status = 'Sent') = (sent_at IS NOT NULL) AND (status = 'Sent') = (recipient_count > 0)");
                    table.CheckConstraint("ck_dues_reminders_skipped", "(status = 'Skipped') = (skip_reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_dues_reminders_enrollment_tenant_id_enrollment_id_student_i",
                        columns: x => new { x.tenant_id, x.enrollment_id, x.student_id, x.group_id },
                        principalSchema: "enrollments",
                        principalTable: "enrollments",
                        principalColumns: new[] { "tenant_id", "id", "student_id", "group_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dues_reminders_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "password_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_password_tokens_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dues_reminders_tenant_id_enrollment_id_period_kind",
                schema: "billing",
                table: "dues_reminders",
                columns: new[] { "tenant_id", "enrollment_id", "period", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dues_reminders_tenant_id_enrollment_id_student_id_group_id",
                schema: "billing",
                table: "dues_reminders",
                columns: new[] { "tenant_id", "enrollment_id", "student_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_dues_reminders_tenant_id_student_id",
                schema: "billing",
                table: "dues_reminders",
                columns: new[] { "tenant_id", "student_id" });

            migrationBuilder.CreateIndex(
                name: "ix_password_tokens_expires_at",
                schema: "identity",
                table: "password_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_password_tokens_token_hash",
                schema: "identity",
                table: "password_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_tokens_user_id",
                schema: "identity",
                table: "password_tokens",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dues_reminders",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "password_tokens",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "password_reset_requested_at",
                schema: "identity",
                table: "users");
        }
    }
}
