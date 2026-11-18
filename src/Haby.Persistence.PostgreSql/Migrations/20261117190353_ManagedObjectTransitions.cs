using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ManagedObjectTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "managed_object_states",
                columns: table => new
                {
                    managed_workload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin_generation = table.Column<long>(type: "bigint", nullable: false),
                    origin_operation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    format = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_object_states", x => new { x.managed_workload_id, x.role });
                    table.CheckConstraint("CK_managed_object_state", "format > 0 AND generation > origin_generation AND octet_length(payload) BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_managed_object_states_durable_action_intents_deployment_id_~",
                        columns: x => new { x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_action_intents",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_object_states_durable_action_intents_deployment_id~1",
                        columns: x => new { x.deployment_id, x.origin_generation, x.origin_operation },
                        principalTable: "durable_action_intents",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_object_states_managed_workloads_managed_workload_id",
                        column: x => x.managed_workload_id,
                        principalTable: "managed_workloads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_managed_object_states_deployment_id_generation_operation_id",
                table: "managed_object_states",
                columns: new[] { "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_object_states_deployment_id_origin_generation_origi~",
                table: "managed_object_states",
                columns: new[] { "deployment_id", "origin_generation", "origin_operation" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_object_states");
        }
    }
}
