using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PrepareWorkloadConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workload_configuration_recipients",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    component = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    workload = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workload_configuration_recipients", x => new { x.deployment_id, x.generation, x.component, x.workload, x.document });
                    table.ForeignKey(
                        name: "FK_workload_configuration_recipients_application_deployment_ge~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workload_configuration_recipients_configuration_document_re~",
                        column: x => x.revision_id,
                        principalTable: "configuration_document_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workload_configuration_recipients_generation_stage_checkpoi~",
                        column: x => x.checkpoint_id,
                        principalTable: "generation_stage_checkpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workload_configuration_recipients_checkpoint_id",
                table: "workload_configuration_recipients",
                column: "checkpoint_id");

            migrationBuilder.CreateIndex(
                name: "IX_workload_configuration_recipients_revision_id",
                table: "workload_configuration_recipients",
                column: "revision_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workload_configuration_recipients");
        }
    }
}
