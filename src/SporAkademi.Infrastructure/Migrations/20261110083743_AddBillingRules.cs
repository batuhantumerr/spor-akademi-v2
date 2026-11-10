using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_rules",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    second_child_percent = table.Column<int>(type: "integer", nullable: false),
                    third_and_later_child_percent = table.Column<int>(type: "integer", nullable: false),
                    single_charge_on_group_change = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_rules", x => x.id);
                    table.CheckConstraint("ck_billing_rules_second_child_percent", "second_child_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_billing_rules_third_and_later_child_percent", "third_and_later_child_percent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_billing_rules_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_billing_rules_tenant_id_effective_from",
                schema: "billing",
                table: "billing_rules",
                columns: new[] { "tenant_id", "effective_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_rules",
                schema: "billing");
        }
    }
}
