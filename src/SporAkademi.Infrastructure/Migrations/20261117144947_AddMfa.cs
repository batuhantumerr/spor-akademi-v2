using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SporAkademi.Infrastructure.Persistence;

#nullable disable

namespace SporAkademi.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// P5-01 (DEC-600 – DEC-609): the second factor. Encrypted TOTP secrets, hashed recovery codes and the challenge between
    /// the password and the second step of a login; row level security follows the owning user.
    /// </summary>
    public partial class AddMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "mfa_enabled",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "mfa_challenges",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_challenges", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_challenges_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfa_credentials",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_ciphertext = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_step = table.Column<long>(type: "bigint", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_credentials", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_credentials_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfa_recovery_codes",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_recovery_codes_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mfa_challenges_expires_at",
                schema: "identity",
                table: "mfa_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_challenges_token_hash",
                schema: "identity",
                table: "mfa_challenges",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mfa_challenges_user_id",
                schema: "identity",
                table: "mfa_challenges",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_credentials_user_id",
                schema: "identity",
                table: "mfa_credentials",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mfa_recovery_codes_user_id",
                schema: "identity",
                table: "mfa_recovery_codes",
                column: "user_id");

            // P5-01: like the other tables owned by a user, a row is visible only while its user is (DEC-527).
            migrationBuilder.Sql(RowLevelSecurity.ForOwnedTable("identity", "mfa_credentials", "identity.users", "user_id"));
            migrationBuilder.Sql(RowLevelSecurity.ForOwnedTable("identity", "mfa_recovery_codes", "identity.users", "user_id"));
            migrationBuilder.Sql(RowLevelSecurity.ForOwnedTable("identity", "mfa_challenges", "identity.users", "user_id"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.Drop("identity", "mfa_challenges"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("identity", "mfa_recovery_codes"));
            migrationBuilder.Sql(RowLevelSecurity.Drop("identity", "mfa_credentials"));

            migrationBuilder.DropTable(
                name: "mfa_challenges",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "mfa_credentials",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "mfa_recovery_codes",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "mfa_enabled",
                schema: "identity",
                table: "users");
        }
    }
}
