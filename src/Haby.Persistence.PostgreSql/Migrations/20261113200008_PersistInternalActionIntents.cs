using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistInternalActionIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_generation_stage_checkpoints_id",
                table: "generation_stage_checkpoints");

            migrationBuilder.AddColumn<bool>(
                name: "internal_effects",
                table: "deployment_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "internal_effects",
                table: "application_deployment_generations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_generation_stage_checkpoints_id",
                table: "generation_stage_checkpoints",
                column: "id");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_durable_create_operation_attempts_attempt_id_deployment_id_~",
                table: "durable_create_operation_attempts",
                columns: new[] { "attempt_id", "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateTable(
                name: "durable_action_intents",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    marker = table.Column<Guid>(type: "uuid", nullable: false),
                    managed_identity = table.Column<Guid>(type: "uuid", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    subject_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    component = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    subject_contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    configuration_contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    implementation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action_contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    input_format = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    output_contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    observation_recovery = table.Column<bool>(type: "boolean", nullable: false),
                    checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(67)", maxLength: 67, nullable: false),
                    placement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_action_intents", x => new { x.deployment_id, x.generation, x.operation_id });
                    table.CheckConstraint("CK_action_intent_bounds", "format_version = 1 AND octet_length(payload) BETWEEN 1 AND 1048576 AND length(request_id) BETWEEN 1 AND 128 AND marker <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_action_intent_subject", "subject_kind IN ('Resource', 'Workload') AND (subject_kind = 'Workload' OR component = '')");
                    table.ForeignKey(
                        name: "FK_durable_action_intents_durable_create_operations_deployment~",
                        columns: x => new { x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_create_operations",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_action_intents_generation_stage_checkpoints_checkpo~",
                        column: x => x.checkpoint_id,
                        principalTable: "generation_stage_checkpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_action_intents_placement_revisions_placement_id",
                        column: x => x.placement_id,
                        principalTable: "placement_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_action_intents_provider_configuration_revisions_pro~",
                        columns: x => new { x.provider_id, x.configuration_revision },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "internal_plan_inputs",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    placement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_internal_plan_inputs", x => new { x.plan_id, x.subject });
                    table.ForeignKey(
                        name: "FK_internal_plan_inputs_deployment_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "deployment_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_internal_plan_inputs_placement_revisions_placement_id",
                        column: x => x.placement_id,
                        principalTable: "placement_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_internal_plan_inputs_provider_configuration_revisions_provi~",
                        columns: x => new { x.provider_id, x.configuration_revision },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "durable_operation_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<long>(type: "bigint", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    receipt = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_operation_observations", x => x.id);
                    table.CheckConstraint("CK_action_observation_bounds", "format_version = 1 AND outcome IN ('Confirmed', 'Unavailable', 'NotConfirmed', 'Refused') AND confirmed = (outcome = 'Confirmed') AND ordinal > 0 AND length(code) BETWEEN 1 AND 128 AND (receipt IS NULL OR octet_length(receipt) BETWEEN 1 AND 1048576) AND confirmed = (receipt IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_durable_operation_observations_durable_action_intents_deplo~",
                        columns: x => new { x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_action_intents",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_durable_operation_observations_durable_create_operation_att~",
                        columns: x => new { x.attempt_id, x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_create_operation_attempts",
                        principalColumns: new[] { "attempt_id", "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_durable_action_intents_checkpoint_id",
                table: "durable_action_intents",
                column: "checkpoint_id");

            migrationBuilder.CreateIndex(
                name: "IX_durable_action_intents_deployment_id_generation_request_id",
                table: "durable_action_intents",
                columns: new[] { "deployment_id", "generation", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_action_intents_deployment_id_generation_subject_kin~",
                table: "durable_action_intents",
                columns: new[] { "deployment_id", "generation", "subject_kind", "component", "local_id", "action_contract" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_action_intents_marker",
                table: "durable_action_intents",
                column: "marker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_action_intents_placement_id",
                table: "durable_action_intents",
                column: "placement_id");

            migrationBuilder.CreateIndex(
                name: "IX_durable_action_intents_provider_id_configuration_revision",
                table: "durable_action_intents",
                columns: new[] { "provider_id", "configuration_revision" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_operation_observations_attempt_id_deployment_id_gen~",
                table: "durable_operation_observations",
                columns: new[] { "attempt_id", "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_operation_observations_deployment_id_generation_op~1",
                table: "durable_operation_observations",
                columns: new[] { "deployment_id", "generation", "operation_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_operation_observations_deployment_id_generation_ope~",
                table: "durable_operation_observations",
                columns: new[] { "deployment_id", "generation", "operation_id" },
                unique: true,
                filter: "confirmed");

            migrationBuilder.CreateIndex(
                name: "IX_internal_plan_inputs_placement_id",
                table: "internal_plan_inputs",
                column: "placement_id");

            migrationBuilder.CreateIndex(
                name: "IX_internal_plan_inputs_provider_id_configuration_revision",
                table: "internal_plan_inputs",
                columns: new[] { "provider_id", "configuration_revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "durable_operation_observations");

            migrationBuilder.DropTable(
                name: "internal_plan_inputs");

            migrationBuilder.DropTable(
                name: "durable_action_intents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_generation_stage_checkpoints_id",
                table: "generation_stage_checkpoints");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_durable_create_operation_attempts_attempt_id_deployment_id_~",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropColumn(
                name: "internal_effects",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "internal_effects",
                table: "application_deployment_generations");

            migrationBuilder.CreateIndex(
                name: "IX_generation_stage_checkpoints_id",
                table: "generation_stage_checkpoints",
                column: "id",
                unique: true);
        }
    }
}
