using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SharedCredentialAuthorityTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE secrets DROP CONSTRAINT ck_secrets_typed_shape;
                ALTER TABLE secrets ADD CONSTRAINT ck_secrets_typed_shape CHECK (
                  sensitivity = 'Credential' AND content_type = 'application/octet-stream'
                  AND storage_backend = 'PostgreSqlPlaintext' AND ownership_policy = 'Managed'
                  AND ((owner = 'Environment' AND purpose IN ('ProviderAdministrator', 'Configuration', 'SharedApplicationCredential')
                        AND (purpose <> 'SharedApplicationCredential' OR name IS NOT NULL)
                        AND application_record_id IS NULL AND resource_local_id IS NULL)
                    OR (owner = 'Resource' AND purpose = 'ApplicationLogin' AND name IS NULL
                        AND application_record_id IS NOT NULL AND resource_local_id IS NOT NULL
                        AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')));
                """);
            migrationBuilder.AddUniqueConstraint(
                name: "AK_provider_revision_secret_refs_provider_instance_id_revision~",
                table: "provider_revision_secret_refs",
                columns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id" });

            migrationBuilder.CreateTable(
                name: "shared_credential_bindings",
                columns: table => new
                {
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    slot = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    use = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_credential_bindings", x => new { x.provider_instance_id, x.revision_number, x.slot });
                    table.CheckConstraint("ck_shared_credential_actor", "length(actor) BETWEEN 1 AND 256");
                    table.CheckConstraint("ck_shared_credential_use", "use IN ('SystemAccess', 'ApplicationDelivery')");
                    table.ForeignKey(
                        name: "FK_shared_credential_bindings_provider_revision_secret_refs_pr~",
                        columns: x => new { x.provider_instance_id, x.revision_number, x.slot, x.environment_record_id, x.secret_id, x.secret_version_id },
                        principalTable: "provider_revision_secret_refs",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shared_credential_revocations",
                columns: table => new
                {
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    slot = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_credential_revocations", x => new { x.provider_instance_id, x.revision_number, x.slot });
                    table.CheckConstraint("ck_shared_revocation_actor", "length(actor) BETWEEN 1 AND 256");
                    table.ForeignKey(
                        name: "FK_shared_credential_revocations_shared_credential_bindings_pr~",
                        columns: x => new { x.provider_instance_id, x.revision_number, x.slot },
                        principalTable: "shared_credential_bindings",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "slot" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shared_credential_bindings_provider_instance_id_revision_nu~",
                table: "shared_credential_bindings",
                columns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CHECK validation refuses downgrade while shared-purpose data remains; no stored workflow.
            migrationBuilder.Sql("""
                ALTER TABLE secrets DROP CONSTRAINT ck_secrets_typed_shape;
                ALTER TABLE secrets ADD CONSTRAINT ck_secrets_typed_shape CHECK (
                  sensitivity = 'Credential' AND content_type = 'application/octet-stream'
                  AND storage_backend = 'PostgreSqlPlaintext' AND ownership_policy = 'Managed'
                  AND ((owner = 'Environment' AND purpose IN ('ProviderAdministrator', 'Configuration')
                        AND application_record_id IS NULL AND resource_local_id IS NULL)
                    OR (owner = 'Resource' AND purpose = 'ApplicationLogin' AND name IS NULL
                        AND application_record_id IS NOT NULL AND resource_local_id IS NOT NULL
                        AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')));
                """);
            migrationBuilder.DropTable(
                name: "shared_credential_revocations");

            migrationBuilder.DropTable(
                name: "shared_credential_bindings");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_provider_revision_secret_refs_provider_instance_id_revision~",
                table: "provider_revision_secret_refs");
        }
    }
}
