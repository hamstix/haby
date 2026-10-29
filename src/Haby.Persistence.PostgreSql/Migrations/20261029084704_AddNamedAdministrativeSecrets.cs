using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddNamedAdministrativeSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_secrets_environment_record_id",
                table: "secrets");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "secrets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "secrets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "secret_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "number",
                table: "secret_versions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "secret_creation_receipts",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_creation_receipts", x => new { x.environment_record_id, x.operation, x.request_id });
                    table.ForeignKey(
                        name: "FK_secret_creation_receipts_secret_versions_secret_id_version_~",
                        columns: x => new { x.secret_id, x.version_id },
                        principalTable: "secret_versions",
                        principalColumns: new[] { "secret_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_secret_creation_receipts_secrets_secret_id_environment_reco~",
                        columns: x => new { x.secret_id, x.environment_record_id },
                        principalTable: "secrets",
                        principalColumns: new[] { "id", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secrets_environment_record_id_name",
                table: "secrets",
                columns: new[] { "environment_record_id", "name" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_secrets_name",
                table: "secrets",
                sql: "name IS NULL OR name ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'");

            migrationBuilder.CreateIndex(
                name: "IX_secret_versions_secret_id_number",
                table: "secret_versions",
                columns: new[] { "secret_id", "number" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_secret_versions_number",
                table: "secret_versions",
                sql: "number IS NULL OR number > 0");

            migrationBuilder.CreateIndex(
                name: "IX_secret_creation_receipts_secret_id_environment_record_id",
                table: "secret_creation_receipts",
                columns: new[] { "secret_id", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_secret_creation_receipts_secret_id_version_id",
                table: "secret_creation_receipts",
                columns: new[] { "secret_id", "version_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "secret_creation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_secrets_environment_record_id_name",
                table: "secrets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_secrets_name",
                table: "secrets");

            migrationBuilder.DropIndex(
                name: "IX_secret_versions_secret_id_number",
                table: "secret_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_secret_versions_number",
                table: "secret_versions");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "name",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "secret_versions");

            migrationBuilder.DropColumn(
                name: "number",
                table: "secret_versions");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_environment_record_id",
                table: "secrets",
                column: "environment_record_id");
        }
    }
}
