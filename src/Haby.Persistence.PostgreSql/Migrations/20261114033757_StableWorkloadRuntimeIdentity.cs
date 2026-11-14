using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class StableWorkloadRuntimeIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installation_identity",
                columns: table => new
                {
                    singleton = table.Column<int>(type: "integer", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_identity", x => x.singleton);
                    table.UniqueConstraint("AK_installation_identity_id", x => x.id);
                    table.CheckConstraint("CK_installation_singleton", "singleton = 1 AND id <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateTable(
                name: "managed_workloads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    workload = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_format = table.Column<int>(type: "integer", nullable: false),
                    target_payload = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_workloads", x => x.id);
                    table.ForeignKey(
                        name: "FK_managed_workloads_application_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "application_deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_workloads_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_workloads_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_workloads_provider_instances_provider_id",
                        column: x => x.provider_id,
                        principalTable: "provider_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "owned_objects",
                columns: table => new
                {
                    managed_workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    format = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owned_objects", x => new { x.managed_workload_id, x.role });
                    table.CheckConstraint("CK_owned_object_payload", "format > 0 AND octet_length(payload) BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_owned_objects_durable_action_intents_deployment_id_generati~",
                        columns: x => new { x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_action_intents",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_owned_objects_managed_workloads_managed_workload_id",
                        column: x => x.managed_workload_id,
                        principalTable: "managed_workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "runtime_identity_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    managed_workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    authenticator = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    audience = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    format = table.Column<int>(type: "integer", nullable: false),
                    validation_payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_identity_bindings", x => x.id);
                    table.CheckConstraint("CK_runtime_binding", "format > 0 AND status = 'Prepared' AND octet_length(validation_payload) BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_runtime_identity_bindings_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_runtime_identity_bindings_installation_identity_installatio~",
                        column: x => x.installation_id,
                        principalTable: "installation_identity",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_runtime_identity_bindings_owned_objects_managed_workload_id~",
                        columns: x => new { x.managed_workload_id, x.role },
                        principalTable: "owned_objects",
                        principalColumns: new[] { "managed_workload_id", "role" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_runtime_identity_bindings_provider_configuration_revisions_~",
                        columns: x => new { x.provider_id, x.configuration_revision },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_managed_workloads_application_id",
                table: "managed_workloads",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "IX_managed_workloads_deployment_id_component_workload",
                table: "managed_workloads",
                columns: new[] { "deployment_id", "component", "workload" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_workloads_environment_id",
                table: "managed_workloads",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_managed_workloads_provider_id",
                table: "managed_workloads",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_owned_objects_deployment_id_generation_operation_id",
                table: "owned_objects",
                columns: new[] { "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_identity_bindings_environment_id",
                table: "runtime_identity_bindings",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_identity_bindings_installation_id",
                table: "runtime_identity_bindings",
                column: "installation_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_identity_bindings_managed_workload_id",
                table: "runtime_identity_bindings",
                column: "managed_workload_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_identity_bindings_managed_workload_id_role",
                table: "runtime_identity_bindings",
                columns: new[] { "managed_workload_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_identity_bindings_provider_id_configuration_revision",
                table: "runtime_identity_bindings",
                columns: new[] { "provider_id", "configuration_revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_identity_bindings");

            migrationBuilder.DropTable(
                name: "installation_identity");

            migrationBuilder.DropTable(
                name: "owned_objects");

            migrationBuilder.DropTable(
                name: "managed_workloads");
        }
    }
}
