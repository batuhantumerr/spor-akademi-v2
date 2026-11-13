using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SporAkademi.Infrastructure.Persistence;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSchoolFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "school_features",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_school_features", x => x.id);
                    table.ForeignKey(
                        name: "fk_school_features_schools_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "schools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_school_features_tenant_id_key",
                schema: "tenancy",
                table: "school_features",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            // P4-10 (DEC-581): a school reads its own values; the Platform Admin (platform scope) reads and writes all.
            migrationBuilder.Sql(RowLevelSecurity.ForPlatformTable("tenancy", "school_features"));
            // Defense in depth: a school's scope reads its values but cannot write them; only the Platform Admin can.
            migrationBuilder.Sql(RowLevelSecurity.PlatformWritesOnly("tenancy", "school_features"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.DropPlatformWritesOnly("tenancy", "school_features"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("tenancy", "school_features"));

            migrationBuilder.DropTable(
                name: "school_features",
                schema: "tenancy");
        }
    }
}
