using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaintextSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "secret_payloads",
                columns: table => new
                {
                    secret_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_payloads", x => x.secret_version_id);
                });

            migrationBuilder.CreateTable(
                name: "secret_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protection_format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secret_versions", x => x.id);
                    table.UniqueConstraint("AK_secret_versions_secret_id_id", x => new { x.secret_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sensitivity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_backend = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ownership_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secrets", x => x.id);
                    table.ForeignKey(
                        name: "FK_secrets_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_secrets_secret_versions_id_current_version_id",
                        columns: x => new { x.id, x.current_version_id },
                        principalTable: "secret_versions",
                        principalColumns: new[] { "secret_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environments_environment_id",
                table: "environments",
                column: "environment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_secrets_environment_record_id",
                table: "secrets",
                column: "environment_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_secrets_id_current_version_id",
                table: "secrets",
                columns: new[] { "id", "current_version_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_secret_payloads_secret_versions_secret_version_id",
                table: "secret_payloads",
                column: "secret_version_id",
                principalTable: "secret_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_secret_versions_secrets_secret_id",
                table: "secret_versions",
                column: "secret_id",
                principalTable: "secrets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE secrets ADD CONSTRAINT CK_secrets_pilot_shape CHECK
                (owner = 'Environment' AND purpose = 'ProviderAdministrator' AND sensitivity = 'Credential'
                 AND content_type = 'application/octet-stream' AND storage_backend = 'PostgreSqlPlaintext'
                 AND ownership_policy = 'Managed');
                CREATE FUNCTION reject_secret_version_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Secret versions are immutable'; END $$;
                CREATE TRIGGER secret_versions_immutable BEFORE UPDATE OR DELETE ON secret_versions
                    FOR EACH ROW EXECUTE FUNCTION reject_secret_version_mutation();
                CREATE TRIGGER secret_payloads_immutable BEFORE UPDATE OR DELETE ON secret_payloads
                    FOR EACH ROW EXECUTE FUNCTION reject_secret_version_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER secret_versions_immutable ON secret_versions; DROP TRIGGER secret_payloads_immutable ON secret_payloads; DROP FUNCTION reject_secret_version_mutation();");
            migrationBuilder.DropForeignKey(
                name: "FK_secrets_secret_versions_id_current_version_id",
                table: "secrets");

            migrationBuilder.DropTable(
                name: "secret_payloads");

            migrationBuilder.DropTable(
                name: "secret_versions");

            migrationBuilder.DropTable(
                name: "secrets");

            migrationBuilder.DropTable(
                name: "environments");
        }
    }
}
