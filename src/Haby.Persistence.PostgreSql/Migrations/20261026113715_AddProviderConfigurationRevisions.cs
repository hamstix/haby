using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderConfigurationRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_secrets_id_environment_record_id",
                table: "secrets",
                columns: new[] { "id", "environment_record_id" });

            migrationBuilder.CreateTable(
                name: "provider_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_kind_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_format_version = table.Column<int>(type: "integer", nullable: false),
                    target_identity = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_instances", x => x.id);
                    table.UniqueConstraint("AK_provider_instances_id_environment_record_id", x => new { x.id, x.environment_record_id });
                    table.ForeignKey(
                        name: "FK_provider_instances_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_configuration_revisions",
                columns: table => new
                {
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    implementation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    configuration_shape_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload_format_version = table.Column<int>(type: "integer", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    is_sealed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_configuration_revisions", x => new { x.provider_instance_id, x.revision_number });
                    table.UniqueConstraint("AK_provider_configuration_revisions_provider_instance_id_revis~", x => new { x.provider_instance_id, x.revision_number, x.environment_record_id });
                    table.ForeignKey(
                        name: "FK_provider_configuration_revisions_provider_instances_provide~",
                        columns: x => new { x.provider_instance_id, x.environment_record_id },
                        principalTable: "provider_instances",
                        principalColumns: new[] { "id", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_revision_secret_refs",
                columns: table => new
                {
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    slot = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_version_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_revision_secret_refs", x => new { x.provider_instance_id, x.revision_number, x.slot });
                    table.ForeignKey(
                        name: "FK_provider_revision_secret_refs_provider_configuration_revisi~",
                        columns: x => new { x.provider_instance_id, x.revision_number, x.environment_record_id },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_revision_secret_refs_secret_versions_secret_id_sec~",
                        columns: x => new { x.secret_id, x.secret_version_id },
                        principalTable: "secret_versions",
                        principalColumns: new[] { "secret_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_revision_secret_refs_secrets_secret_id_environment~",
                        columns: x => new { x.secret_id, x.environment_record_id },
                        principalTable: "secrets",
                        principalColumns: new[] { "id", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_configuration_revisions_provider_instance_id_envir~",
                table: "provider_configuration_revisions",
                columns: new[] { "provider_instance_id", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_instances_environment_record_id",
                table: "provider_instances",
                column: "environment_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_revision_secret_refs_provider_instance_id_revision~",
                table: "provider_revision_secret_refs",
                columns: new[] { "provider_instance_id", "revision_number", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_revision_secret_refs_secret_id_environment_record_~",
                table: "provider_revision_secret_refs",
                columns: new[] { "secret_id", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_revision_secret_refs_secret_id_secret_version_id",
                table: "provider_revision_secret_refs",
                columns: new[] { "secret_id", "secret_version_id" });

            migrationBuilder.Sql("""
                ALTER TABLE provider_instances ADD CONSTRAINT CK_provider_instances_identity
                    CHECK (length(btrim(provider_kind_id)) > 0 AND target_format_version > 0
                        AND length(btrim(target_identity)) > 0 AND substring(id::text from 15 for 1) = '7');
                ALTER TABLE provider_configuration_revisions ADD CONSTRAINT CK_provider_configuration_revisions_envelope
                    CHECK (revision_number > 0 AND length(btrim(implementation_id)) > 0
                        AND length(btrim(configuration_shape_id)) > 0 AND payload_format_version > 0
                        AND jsonb_typeof(payload_json::jsonb) = 'object');
                ALTER TABLE provider_revision_secret_refs ADD CONSTRAINT CK_provider_revision_secret_refs_slot
                    CHECK (length(btrim(slot)) > 0);
                CREATE FUNCTION reject_provider_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Provider rows are immutable'; END $$;
                CREATE TRIGGER provider_instances_immutable BEFORE UPDATE OR DELETE ON provider_instances
                    FOR EACH ROW EXECUTE FUNCTION reject_provider_mutation();
                CREATE TRIGGER provider_revision_secret_refs_immutable BEFORE UPDATE OR DELETE
                    ON provider_revision_secret_refs FOR EACH ROW EXECUTE FUNCTION reject_provider_mutation();
                CREATE FUNCTION protect_provider_revision_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD.is_sealed = FALSE AND NEW.is_sealed = TRUE
                        AND (to_jsonb(OLD) - 'is_sealed') = (to_jsonb(NEW) - 'is_sealed') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Provider revisions are immutable';
                END $$;
                CREATE TRIGGER provider_configuration_revisions_immutable BEFORE UPDATE OR DELETE
                    ON provider_configuration_revisions FOR EACH ROW EXECUTE FUNCTION protect_provider_revision_update();
                CREATE FUNCTION check_provider_secret_ref_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM provider_configuration_revisions
                        WHERE provider_instance_id = NEW.provider_instance_id
                            AND revision_number = NEW.revision_number AND is_sealed = FALSE) THEN
                        RAISE EXCEPTION 'Provider SecretRefs require an unsealed revision';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER provider_revision_secret_refs_before_seal BEFORE INSERT ON provider_revision_secret_refs
                    FOR EACH ROW EXECUTE FUNCTION check_provider_secret_ref_insert();
                CREATE FUNCTION require_provider_revision_seal() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM provider_configuration_revisions
                        WHERE provider_instance_id = NEW.provider_instance_id
                            AND revision_number = NEW.revision_number AND is_sealed = TRUE) THEN
                        RAISE EXCEPTION 'Provider revision must be sealed before commit';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER provider_configuration_revisions_sealed AFTER INSERT
                    ON provider_configuration_revisions DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION require_provider_revision_seal();
                CREATE FUNCTION check_provider_revision_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE latest bigint;
                BEGIN
                    IF NEW.is_sealed THEN
                        RAISE EXCEPTION 'Provider revision must be inserted unsealed';
                    END IF;
                    PERFORM 1 FROM provider_instances WHERE id = NEW.provider_instance_id FOR UPDATE;
                    SELECT max(revision_number) INTO latest FROM provider_configuration_revisions
                        WHERE provider_instance_id = NEW.provider_instance_id;
                    IF (latest IS NULL AND NEW.revision_number <> 1)
                        OR (latest IS NOT NULL AND NEW.revision_number <= latest) THEN
                        RAISE EXCEPTION 'Provider revision number is not increasing';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER provider_configuration_revisions_ordered BEFORE INSERT
                    ON provider_configuration_revisions FOR EACH ROW EXECUTE FUNCTION check_provider_revision_insert();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER provider_configuration_revisions_ordered ON provider_configuration_revisions;
                DROP FUNCTION check_provider_revision_insert();
                DROP TRIGGER provider_configuration_revisions_sealed ON provider_configuration_revisions;
                DROP FUNCTION require_provider_revision_seal();
                DROP TRIGGER provider_revision_secret_refs_before_seal ON provider_revision_secret_refs;
                DROP FUNCTION check_provider_secret_ref_insert();
                DROP TRIGGER provider_configuration_revisions_immutable ON provider_configuration_revisions;
                DROP FUNCTION protect_provider_revision_update();
                DROP TRIGGER provider_revision_secret_refs_immutable ON provider_revision_secret_refs;
                DROP TRIGGER provider_instances_immutable ON provider_instances;
                DROP FUNCTION reject_provider_mutation();
                """);
            migrationBuilder.DropTable(
                name: "provider_revision_secret_refs");

            migrationBuilder.DropTable(
                name: "provider_configuration_revisions");

            migrationBuilder.DropTable(
                name: "provider_instances");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_secrets_id_environment_record_id",
                table: "secrets");
        }
    }
}
