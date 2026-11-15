using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistRolloutObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints");

            migrationBuilder.CreateTable(
                name: "generation_observation_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    previous = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    budget_seconds = table.Column<int>(type: "integer", nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    origin_json = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_observation_attempts", x => x.id);
                    table.UniqueConstraint("AK_generation_observation_attempts_id_deployment_id_generation", x => new { x.id, x.deployment_id, x.generation });
                    table.CheckConstraint("CK_observation_attempt_bounds", "ordinal > 0 AND budget_seconds BETWEEN 1 AND 86400 AND deadline = started_at + budget_seconds * interval '1 second' AND length(subject) BETWEEN 1 AND 256 AND octet_length(origin_json) BETWEEN 1 AND 16384 AND length(actor) BETWEEN 1 AND 256");
                    table.ForeignKey(
                        name: "FK_generation_observation_attempts_durable_action_intents_depl~",
                        columns: x => new { x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_action_intents",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generation_observation_attempts_generation_observation_atte~",
                        columns: x => new { x.previous, x.deployment_id, x.generation },
                        principalTable: "generation_observation_attempts",
                        principalColumns: new[] { "id", "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generation_observation_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    input_json = table.Column<string>(type: "text", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_observation_records", x => x.id);
                    table.CheckConstraint("CK_observation_record_bounds", "kind IN ('Request', 'Read', 'Expired') AND ordinal >= 0 AND length(request) BETWEEN 1 AND 128 AND octet_length(input_json) <= 131072 AND (payload IS NULL OR octet_length(payload) BETWEEN 1 AND 2097152) AND (finished_at IS NULL) = (payload IS NULL)");
                    table.ForeignKey(
                        name: "FK_generation_observation_records_application_deployment_gener~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_generation_observation_records_generation_observation_attem~",
                        columns: x => new { x.attempt_id, x.deployment_id, x.generation },
                        principalTable: "generation_observation_attempts",
                        principalColumns: new[] { "id", "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints",
                sql: "generation > 0 AND format_version = 1 AND stage IN ('ResourcesReady', 'WorkloadsSubmitted', 'RolloutReady')");

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_attempts_deployment_id_generation_op~",
                table: "generation_observation_attempts",
                columns: new[] { "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_attempts_deployment_id_generation_su~",
                table: "generation_observation_attempts",
                columns: new[] { "deployment_id", "generation", "subject", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_attempts_previous_deployment_id_gene~",
                table: "generation_observation_attempts",
                columns: new[] { "previous", "deployment_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_records_attempt_id",
                table: "generation_observation_records",
                column: "attempt_id",
                unique: true,
                filter: "kind = 'Expired'");

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_records_attempt_id_deployment_id_gen~",
                table: "generation_observation_records",
                columns: new[] { "attempt_id", "deployment_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_records_deployment_id_generation_re~1",
                table: "generation_observation_records",
                columns: new[] { "deployment_id", "generation", "request", "subject", "ordinal" },
                unique: true,
                filter: "kind = 'Read'");

            migrationBuilder.CreateIndex(
                name: "IX_generation_observation_records_deployment_id_generation_req~",
                table: "generation_observation_records",
                columns: new[] { "deployment_id", "generation", "request" },
                unique: true,
                filter: "kind = 'Request'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generation_observation_records");

            migrationBuilder.DropTable(
                name: "generation_observation_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints");

            migrationBuilder.AddCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints",
                sql: "generation > 0 AND format_version = 1 AND stage = 'ResourcesReady'");
        }
    }
}
