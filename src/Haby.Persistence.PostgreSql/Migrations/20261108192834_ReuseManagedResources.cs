using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ReuseManagedResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_change_set_commits_deployment_id_generation",
                table: "change_set_commits");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_set_commits_first",
                table: "change_set_commits");

            migrationBuilder.DropCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_deployment_generations_first",
                table: "application_deployment_generations");

            migrationBuilder.AddColumn<long>(
                name: "expected_generation",
                table: "deployment_change_sets",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resource_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_generation = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_management", x => x.id);
                    table.CheckConstraint("CK_resource_management_identity", "source_generation > 0 AND length(resource_local_id) > 0");
                    table.ForeignKey(
                        name: "FK_resource_management_application_deployment_generations_depl~",
                        columns: x => new { x.deployment_id, x.source_generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_management_applications_application_record_id",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_management_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_deployment_change_sets_precondition",
                table: "deployment_change_sets",
                sql: "(expected_absence AND expected_generation IS NULL) OR (NOT expected_absence AND expected_generation > 0 AND expected_generation IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_change_set_commits_deployment_id_generation",
                table: "change_set_commits",
                columns: new[] { "deployment_id", "generation" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_set_commits_first",
                table: "change_set_commits",
                sql: "generation > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments",
                sql: "desired_generation > 0 AND (applied_generation IS NULL OR (applied_generation > 0 AND applied_generation <= desired_generation))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_deployment_generations_first",
                table: "application_deployment_generations",
                sql: "generation > 0 AND presence = 'Present'");

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_application_record_id",
                table: "resource_management",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_deployment_id_source_generation",
                table: "resource_management",
                columns: new[] { "deployment_id", "source_generation" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_environment_record_id_application_recor~",
                table: "resource_management",
                columns: new[] { "environment_record_id", "application_record_id", "resource_local_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_management");

            migrationBuilder.DropCheckConstraint(
                name: "CK_deployment_change_sets_precondition",
                table: "deployment_change_sets");

            migrationBuilder.DropIndex(
                name: "IX_change_set_commits_deployment_id_generation",
                table: "change_set_commits");

            migrationBuilder.DropCheckConstraint(
                name: "CK_change_set_commits_first",
                table: "change_set_commits");

            migrationBuilder.DropCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_deployment_generations_first",
                table: "application_deployment_generations");

            migrationBuilder.DropColumn(
                name: "expected_generation",
                table: "deployment_change_sets");

            migrationBuilder.CreateIndex(
                name: "IX_change_set_commits_deployment_id_generation",
                table: "change_set_commits",
                columns: new[] { "deployment_id", "generation" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_change_set_commits_first",
                table: "change_set_commits",
                sql: "generation = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments",
                sql: "desired_generation = 1 AND (applied_generation IS NULL OR applied_generation = 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_deployment_generations_first",
                table: "application_deployment_generations",
                sql: "generation = 1 AND presence = 'Present'");
        }
    }
}
