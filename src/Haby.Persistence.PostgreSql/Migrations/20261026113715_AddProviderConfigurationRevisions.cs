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
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_instances", x => x.id);
                    table.UniqueConstraint("AK_provider_instances_id_environment_record_id_host_port", x => new { x.id, x.environment_record_id, x.host, x.port });
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
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    shape_version = table.Column<int>(type: "integer", nullable: false),
                    maintenance_database = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    administrator_user_name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    administrator_secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    administrator_secret_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    implementation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_configuration_revisions", x => new { x.provider_instance_id, x.revision_number });
                    table.ForeignKey(
                        name: "FK_provider_configuration_revisions_provider_instances_provide~",
                        columns: x => new { x.provider_instance_id, x.environment_record_id, x.host, x.port },
                        principalTable: "provider_instances",
                        principalColumns: new[] { "id", "environment_record_id", "host", "port" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_configuration_revisions_secret_versions_administra~",
                        columns: x => new { x.administrator_secret_id, x.administrator_secret_version_id },
                        principalTable: "secret_versions",
                        principalColumns: new[] { "secret_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_configuration_revisions_secrets_administrator_secr~",
                        columns: x => new { x.administrator_secret_id, x.environment_record_id },
                        principalTable: "secrets",
                        principalColumns: new[] { "id", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_configuration_revisions_administrator_secret_id_ad~",
                table: "provider_configuration_revisions",
                columns: new[] { "administrator_secret_id", "administrator_secret_version_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_configuration_revisions_administrator_secret_id_en~",
                table: "provider_configuration_revisions",
                columns: new[] { "administrator_secret_id", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_configuration_revisions_provider_instance_id_envir~",
                table: "provider_configuration_revisions",
                columns: new[] { "provider_instance_id", "environment_record_id", "host", "port" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_instances_environment_record_id",
                table: "provider_instances",
                column: "environment_record_id");

            migrationBuilder.Sql("""
                ALTER TABLE provider_instances ADD CONSTRAINT CK_provider_instances_endpoint
                    CHECK (length(btrim(host)) > 0 AND port BETWEEN 1 AND 65535
                        AND substring(id::text from 15 for 1) = '7');
                ALTER TABLE provider_configuration_revisions ADD CONSTRAINT CK_provider_configuration_revisions_shape
                    CHECK (revision_number > 0 AND shape_version = 1 AND length(btrim(host)) > 0
                        AND port BETWEEN 1 AND 65535 AND length(btrim(maintenance_database)) > 0
                        AND length(btrim(administrator_user_name)) > 0 AND length(btrim(implementation_id)) > 0);
                CREATE FUNCTION reject_provider_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Provider rows are immutable'; END $$;
                CREATE TRIGGER provider_instances_immutable BEFORE UPDATE OR DELETE ON provider_instances
                    FOR EACH ROW EXECUTE FUNCTION reject_provider_mutation();
                CREATE TRIGGER provider_configuration_revisions_immutable BEFORE UPDATE OR DELETE
                    ON provider_configuration_revisions FOR EACH ROW EXECUTE FUNCTION reject_provider_mutation();
                CREATE FUNCTION check_provider_revision_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE latest bigint;
                BEGIN
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
                DROP TRIGGER provider_configuration_revisions_immutable ON provider_configuration_revisions;
                DROP TRIGGER provider_instances_immutable ON provider_instances;
                DROP FUNCTION reject_provider_mutation();
                """);
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
