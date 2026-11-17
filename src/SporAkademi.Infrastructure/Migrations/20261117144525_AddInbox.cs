using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SporAkademi.Infrastructure.Persistence;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <summary>
    /// P5-03 (DEC-623): the consumers' inbox (idempotent consumers). Row level security as a platform table like
    /// <c>outbox.messages</c>: a consumer runs under the message's school and sees only its own rows; platform-level
    /// messages (no school) and cleanup run in platform scope.
    /// </summary>
    public partial class AddInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "outbox",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_processed_at",
                schema: "outbox",
                table: "inbox",
                column: "processed_at");

            migrationBuilder.Sql(RowLevelSecurity.ForPlatformTable("outbox", "inbox"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.Drop("outbox", "inbox"));

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "outbox");
        }
    }
}
