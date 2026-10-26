using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableDeploymentPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_manifest_revisions_application_record_id_local_revision_num~",
                table: "manifest_revisions");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_manifest_revisions_application_record_id_local_revision_num~",
                table: "manifest_revisions",
                columns: new[] { "application_record_id", "local_revision_number" });

            migrationBuilder.CreateTable(
                name: "deployment_change_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    expected_absence = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_change_sets", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployment_change_sets_applications_application_record_id",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployment_change_sets_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployment_change_sets_manifest_revisions_application_recor~",
                        columns: x => new { x.application_record_id, x.revision_number },
                        principalTable: "manifest_revisions",
                        principalColumns: new[] { "application_record_id", "local_revision_number" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    provider_alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_revision_number = table.Column<long>(type: "bigint", nullable: false),
                    implementation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    snapshot_format_version = table.Column<int>(type: "integer", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(67)", maxLength: 67, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_plans", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployment_plans_deployment_change_sets_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "deployment_change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployment_plans_provider_configuration_revisions_provider_~",
                        columns: x => new { x.provider_instance_id, x.configuration_revision_number, x.environment_record_id },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_plan_closures",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_plan_closures", x => x.plan_id);
                    table.ForeignKey(
                        name: "FK_deployment_plan_closures_deployment_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "deployment_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_change_sets_application_record_id_revision_number",
                table: "deployment_change_sets",
                columns: new[] { "application_record_id", "revision_number" });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_change_sets_environment_record_id",
                table: "deployment_change_sets",
                column: "environment_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_plans_change_set_id",
                table: "deployment_plans",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_plans_provider_instance_id_configuration_revisio~",
                table: "deployment_plans",
                columns: new[] { "provider_instance_id", "configuration_revision_number", "environment_record_id" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_deployment_planning_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN
                    RAISE EXCEPTION 'Deployment planning records are immutable';
                END $$;
                CREATE TRIGGER immutable_deployment_change_sets BEFORE UPDATE OR DELETE ON deployment_change_sets
                    FOR EACH ROW EXECUTE FUNCTION reject_deployment_planning_mutation();
                CREATE TRIGGER immutable_deployment_plans BEFORE UPDATE OR DELETE ON deployment_plans
                    FOR EACH ROW EXECUTE FUNCTION reject_deployment_planning_mutation();
                CREATE TRIGGER immutable_deployment_plan_closures BEFORE UPDATE OR DELETE ON deployment_plan_closures
                    FOR EACH ROW EXECUTE FUNCTION reject_deployment_planning_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER immutable_deployment_plan_closures ON deployment_plan_closures;
                DROP TRIGGER immutable_deployment_plans ON deployment_plans;
                DROP TRIGGER immutable_deployment_change_sets ON deployment_change_sets;
                DROP FUNCTION reject_deployment_planning_mutation();
                """);
            migrationBuilder.DropTable(
                name: "deployment_plan_closures");

            migrationBuilder.DropTable(
                name: "deployment_plans");

            migrationBuilder.DropTable(
                name: "deployment_change_sets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_manifest_revisions_application_record_id_local_revision_num~",
                table: "manifest_revisions");

            migrationBuilder.CreateIndex(
                name: "IX_manifest_revisions_application_record_id_local_revision_num~",
                table: "manifest_revisions",
                columns: new[] { "application_record_id", "local_revision_number" },
                unique: true);
        }
    }
}
