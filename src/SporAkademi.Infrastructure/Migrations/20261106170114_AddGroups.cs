using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "groups");

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sport_id = table.Column<Guid>(type: "uuid", nullable: false),
                    facility_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coach_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    archived_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    birth_year_from = table.Column<int>(type: "integer", nullable: false),
                    birth_year_to = table.Column<int>(type: "integer", nullable: false),
                    schedule_slots = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                    table.UniqueConstraint("ak_groups_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_groups_birth_years", "birth_year_from <= birth_year_to");
                    table.CheckConstraint("ck_groups_capacity", "capacity BETWEEN 1 AND 1000");
                    table.ForeignKey(
                        name: "fk_groups_facility_tenant_id_facility_id",
                        columns: x => new { x.tenant_id, x.facility_id },
                        principalSchema: "organization",
                        principalTable: "facilities",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_groups_school_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_groups_sport_tenant_id_sport_id",
                        columns: x => new { x.tenant_id, x.sport_id },
                        principalSchema: "organization",
                        principalTable: "sports",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_groups_tenant_id_coach_id",
                schema: "groups",
                table: "groups",
                columns: new[] { "tenant_id", "coach_id" });

            migrationBuilder.CreateIndex(
                name: "ix_groups_tenant_id_facility_id",
                schema: "groups",
                table: "groups",
                columns: new[] { "tenant_id", "facility_id" });

            migrationBuilder.CreateIndex(
                name: "ix_groups_tenant_id_name_key",
                schema: "groups",
                table: "groups",
                columns: new[] { "tenant_id", "name_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_groups_tenant_id_sport_id",
                schema: "groups",
                table: "groups",
                columns: new[] { "tenant_id", "sport_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "groups",
                schema: "groups");
        }
    }
}
