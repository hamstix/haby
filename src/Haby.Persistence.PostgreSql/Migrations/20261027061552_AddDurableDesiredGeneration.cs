using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableDesiredGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_generation = table.Column<long>(type: "bigint", nullable: false),
                    applied_generation = table.Column<long>(type: "bigint", nullable: true),
                    concurrency_token = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_deployments", x => x.id);
                    table.CheckConstraint("CK_application_deployments_desired_generation", "desired_generation = 1 AND applied_generation IS NULL");
                    table.ForeignKey(
                        name: "FK_application_deployments_applications_application_record_id",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_deployments_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_deployment_generations",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_revision_number = table.Column<long>(type: "bigint", nullable: false),
                    implementation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(67)", maxLength: 67, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_deployment_generations", x => new { x.deployment_id, x.generation });
                    table.CheckConstraint("CK_deployment_generations_first", "generation = 1");
                    table.ForeignKey(
                        name: "FK_application_deployment_generations_application_deployments_~",
                        column: x => x.deployment_id,
                        principalTable: "application_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_deployment_generations_deployment_change_sets_c~",
                        column: x => x.change_set_id,
                        principalTable: "deployment_change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_deployment_generations_deployment_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "deployment_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_deployment_generations_manifest_revisions_appli~",
                        columns: x => new { x.application_record_id, x.revision_number },
                        principalTable: "manifest_revisions",
                        principalColumns: new[] { "application_record_id", "local_revision_number" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_deployment_generations_provider_configuration_r~",
                        columns: x => new { x.provider_instance_id, x.configuration_revision_number, x.environment_record_id },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change_set_commits",
                columns: table => new
                {
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_set_commits", x => x.change_set_id);
                    table.CheckConstraint("CK_change_set_commits_first", "generation = 1");
                    table.ForeignKey(
                        name: "FK_change_set_commits_application_deployment_generations_deplo~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_change_set_commits_deployment_change_sets_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "deployment_change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_change_set_commits_deployment_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "deployment_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_deployment_generations_application_record_id_re~",
                table: "application_deployment_generations",
                columns: new[] { "application_record_id", "revision_number" });

            migrationBuilder.CreateIndex(
                name: "IX_application_deployment_generations_change_set_id",
                table: "application_deployment_generations",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_deployment_generations_plan_id",
                table: "application_deployment_generations",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_deployment_generations_provider_instance_id_con~",
                table: "application_deployment_generations",
                columns: new[] { "provider_instance_id", "configuration_revision_number", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_deployments_application_record_id",
                table: "application_deployments",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_deployments_environment_record_id_application_r~",
                table: "application_deployments",
                columns: new[] { "environment_record_id", "application_record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_change_set_commits_deployment_id_generation",
                table: "change_set_commits",
                columns: new[] { "deployment_id", "generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_change_set_commits_plan_id",
                table: "change_set_commits",
                column: "plan_id");

            migrationBuilder.Sql("""
                CREATE TRIGGER immutable_deployment_generations BEFORE UPDATE OR DELETE ON application_deployment_generations
                    FOR EACH ROW EXECUTE FUNCTION reject_deployment_planning_mutation();
                CREATE TRIGGER immutable_change_set_commits BEFORE UPDATE OR DELETE ON change_set_commits
                    FOR EACH ROW EXECUTE FUNCTION reject_deployment_planning_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER immutable_change_set_commits ON change_set_commits;
                DROP TRIGGER immutable_deployment_generations ON application_deployment_generations;
                """);
            migrationBuilder.DropTable(
                name: "change_set_commits");

            migrationBuilder.DropTable(
                name: "application_deployment_generations");

            migrationBuilder.DropTable(
                name: "application_deployments");
        }
    }
}
