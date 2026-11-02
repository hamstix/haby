using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedCredentialSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM secrets) THEN
                    RAISE EXCEPTION 'A fresh catalog is required for typed secret ownership.';
                  END IF;
                END $$;
                ALTER TABLE secrets DROP CONSTRAINT ck_secrets_pilot_shape;
                """);
            migrationBuilder.AddColumn<Guid>(
                name: "application_record_id",
                table: "secrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resource_local_id",
                table: "secrets",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "generated_credential_slots",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    slot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    generation_profile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_credential_slots", x => new { x.environment_record_id, x.application_record_id, x.resource_local_id, x.slot_id });
                    table.UniqueConstraint("AK_generated_credential_slots_secret_id", x => x.secret_id);
                    table.ForeignKey(
                        name: "FK_generated_credential_slots_applications_application_record_~",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generated_credential_slots_secret_versions_secret_id_versio~",
                        columns: x => new { x.secret_id, x.version_id },
                        principalTable: "secret_versions",
                        principalColumns: new[] { "secret_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generated_credential_slots_secrets_secret_id_environment_re~",
                        columns: x => new { x.secret_id, x.environment_record_id },
                        principalTable: "secrets",
                        principalColumns: new[] { "id", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generated_credential_creation_audits",
                columns: table => new
                {
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technical_actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generated_credential_creation_audits", x => x.secret_id);
                    table.ForeignKey(
                        name: "FK_generated_credential_creation_audits_generated_credential_s~",
                        column: x => x.secret_id,
                        principalTable: "generated_credential_slots",
                        principalColumn: "secret_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_secrets_application_record_id",
                table: "secrets",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_generated_credential_slots_application_record_id",
                table: "generated_credential_slots",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_generated_credential_slots_secret_id_environment_record_id",
                table: "generated_credential_slots",
                columns: new[] { "secret_id", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_generated_credential_slots_secret_id_version_id",
                table: "generated_credential_slots",
                columns: new[] { "secret_id", "version_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_secrets_applications_application_record_id",
                table: "secrets",
                column: "application_record_id",
                principalTable: "applications",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                ALTER TABLE secrets ADD CONSTRAINT ck_secrets_typed_shape CHECK (
                  sensitivity = 'Credential' AND content_type = 'application/octet-stream'
                  AND storage_backend = 'PostgreSqlPlaintext' AND ownership_policy = 'Managed'
                  AND ((owner = 'Environment' AND purpose = 'ProviderAdministrator'
                        AND application_record_id IS NULL AND resource_local_id IS NULL)
                    OR (owner = 'Resource' AND purpose = 'ApplicationLogin' AND name IS NULL
                        AND application_record_id IS NOT NULL AND resource_local_id IS NOT NULL
                        AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')));
                ALTER TABLE generated_credential_slots ADD CONSTRAINT ck_generated_slot_shape CHECK (
                  slot_id = 'application-password' AND generation_profile = 'random-password/v1'
                  AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$');
                CREATE FUNCTION guard_generated_slot_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM secrets s JOIN secret_versions v ON v.secret_id = s.id
                    WHERE s.id = NEW.secret_id AND s.environment_record_id = NEW.environment_record_id
                      AND s.application_record_id = NEW.application_record_id AND s.resource_local_id = NEW.resource_local_id
                      AND s.owner = 'Resource' AND s.purpose = 'ApplicationLogin'
                      AND s.current_version_id = NEW.version_id AND v.id = NEW.version_id AND v.number = 1) THEN
                    RAISE EXCEPTION 'Generated credential association is inconsistent';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER generated_credential_slots_validate BEFORE INSERT ON generated_credential_slots
                  FOR EACH ROW EXECUTE FUNCTION guard_generated_slot_insert();
                CREATE TRIGGER generated_credential_slots_immutable BEFORE UPDATE OR DELETE ON generated_credential_slots
                  FOR EACH ROW EXECUTE FUNCTION reject_secret_version_mutation();
                CREATE TRIGGER generated_credential_creation_audits_immutable BEFORE UPDATE OR DELETE ON generated_credential_creation_audits
                  FOR EACH ROW EXECUTE FUNCTION reject_secret_version_mutation();
                CREATE FUNCTION guard_generated_secret_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM generated_credential_slots WHERE secret_id = OLD.id) THEN
                    RAISE EXCEPTION 'Generated credential metadata is immutable';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER generated_secret_immutable BEFORE UPDATE ON secrets
                  FOR EACH ROW EXECUTE FUNCTION guard_generated_secret_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This development model is reset-required; no populated downgrade is supported.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM generated_credential_slots) THEN
                    RAISE EXCEPTION 'A fresh catalog is required; generated material cannot be downgraded.';
                  END IF;
                END $$;
                DROP TRIGGER generated_secret_immutable ON secrets;
                DROP FUNCTION guard_generated_secret_update();
                DROP TRIGGER generated_credential_slots_validate ON generated_credential_slots;
                DROP FUNCTION guard_generated_slot_insert();
                ALTER TABLE secrets DROP CONSTRAINT ck_secrets_typed_shape;
                ALTER TABLE secrets ADD CONSTRAINT ck_secrets_pilot_shape CHECK
                  (owner = 'Environment' AND purpose = 'ProviderAdministrator' AND sensitivity = 'Credential'
                   AND content_type = 'application/octet-stream' AND storage_backend = 'PostgreSqlPlaintext'
                   AND ownership_policy = 'Managed');
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_secrets_applications_application_record_id",
                table: "secrets");

            migrationBuilder.DropTable(
                name: "generated_credential_creation_audits");

            migrationBuilder.DropTable(
                name: "generated_credential_slots");

            migrationBuilder.DropIndex(
                name: "IX_secrets_application_record_id",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "application_record_id",
                table: "secrets");

            migrationBuilder.DropColumn(
                name: "resource_local_id",
                table: "secrets");
        }
    }
}
