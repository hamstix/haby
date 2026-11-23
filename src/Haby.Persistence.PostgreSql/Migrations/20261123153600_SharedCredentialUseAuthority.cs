using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SharedCredentialUseAuthority : Migration
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
                        columns: x => new { x.provider_instance_id, x.revision_number, x.slot },
                        principalTable: "provider_revision_secret_refs",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "slot" },
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
            migrationBuilder.Sql("""
                CREATE FUNCTION check_shared_credential_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM provider_revision_secret_refs r
                        JOIN provider_configuration_revisions p ON p.provider_instance_id = r.provider_instance_id
                            AND p.revision_number = r.revision_number AND p.environment_record_id = r.environment_record_id
                        JOIN secrets s ON s.id = r.secret_id AND s.environment_record_id = r.environment_record_id
                        WHERE r.provider_instance_id = NEW.provider_instance_id AND r.revision_number = NEW.revision_number
                            AND r.slot = NEW.slot AND r.environment_record_id = NEW.environment_record_id
                            AND r.secret_id = NEW.secret_id AND r.secret_version_id = NEW.secret_version_id
                            AND NOT p.is_sealed AND s.owner = 'Environment' AND s.purpose = 'SharedApplicationCredential'
                            AND s.application_record_id IS NULL AND s.resource_local_id IS NULL AND s.name IS NOT NULL
                    ) THEN RAISE EXCEPTION 'Shared credential issuance requires an exact unsealed revision reference'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER shared_credential_bindings_before_seal BEFORE INSERT ON shared_credential_bindings
                    FOR EACH ROW EXECUTE FUNCTION check_shared_credential_insert();
                CREATE TRIGGER shared_credential_bindings_immutable BEFORE UPDATE OR DELETE ON shared_credential_bindings
                    FOR EACH ROW EXECUTE FUNCTION reject_provider_mutation();
                CREATE TRIGGER shared_credential_revocations_immutable BEFORE UPDATE OR DELETE ON shared_credential_revocations
                    FOR EACH ROW EXECUTE FUNCTION reject_provider_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM secrets WHERE purpose = 'SharedApplicationCredential') THEN
                        RAISE EXCEPTION 'Shared credentials prevent downgrade.';
                    END IF;
                END $$;
                ALTER TABLE secrets DROP CONSTRAINT ck_secrets_typed_shape;
                ALTER TABLE secrets ADD CONSTRAINT ck_secrets_typed_shape CHECK (
                  sensitivity = 'Credential' AND content_type = 'application/octet-stream'
                  AND storage_backend = 'PostgreSqlPlaintext' AND ownership_policy = 'Managed'
                  AND ((owner = 'Environment' AND purpose IN ('ProviderAdministrator', 'Configuration')
                    AND application_record_id IS NULL AND resource_local_id IS NULL)
                OR (owner = 'Resource' AND purpose = 'ApplicationLogin' AND name IS NULL
                    AND application_record_id IS NOT NULL AND resource_local_id IS NOT NULL
                    AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')));
                DROP FUNCTION check_shared_credential_insert() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "shared_credential_revocations");

            migrationBuilder.DropTable(
                name: "shared_credential_bindings");
        }
    }
}
