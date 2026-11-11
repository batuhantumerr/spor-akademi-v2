using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddParentAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "account_user_id",
                schema: "students",
                table: "guardians",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_guardians_account_user_id",
                schema: "students",
                table: "guardians",
                column: "account_user_id",
                unique: true,
                filter: "account_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_guardians_account_user_id",
                schema: "students",
                table: "guardians");

            migrationBuilder.DropColumn(
                name: "account_user_id",
                schema: "students",
                table: "guardians");
        }
    }
}
