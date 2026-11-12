using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistResourcesReadyCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old Applied pointers cannot supply the evidence this format requires.
            migrationBuilder.Sql("""
                LOCK TABLE deployment_plans, application_deployment_generations IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM application_deployment_generations) OR EXISTS (SELECT 1 FROM deployment_plans) THEN
                        RAISE EXCEPTION 'Incompatible generation checkpoint history; explicitly initialize a fresh catalog. External resources remain unchanged.';
                    END IF;
                END $$;
                """);
            migrationBuilder.CreateTable(
                name: "generation_stage_checkpoints",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(67)", maxLength: 67, nullable: false),
                    evidence_json = table.Column<string>(type: "text", nullable: false),
                    evidence_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_generation_stage_checkpoints", x => new { x.deployment_id, x.generation, x.stage });
                    table.CheckConstraint("CK_generation_stage_checkpoints_bounds", "length(input_fingerprint) = 67 AND octet_length(evidence_json) <= 2097152");
                    table.CheckConstraint("CK_generation_stage_checkpoints_identity", "generation > 0 AND format_version = 1 AND stage = 'ResourcesReady'");
                    table.ForeignKey(
                        name: "FK_generation_stage_checkpoints_application_deployment_generat~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_generation_stage_checkpoints_id",
                table: "generation_stage_checkpoints",
                column: "id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE deployment_plans, application_deployment_generations IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM deployment_plans) OR EXISTS (SELECT 1 FROM application_deployment_generations) THEN
                        RAISE EXCEPTION 'Generation checkpoint history cannot be downgraded; explicitly initialize a fresh catalog. External resources remain unchanged.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(name: "generation_stage_checkpoints");
        }
    }
}
