using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class InitialRetirementCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints");

            migrationBuilder.AddCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints",
                sql: "generation > 0 AND format_version = 1 AND stage IN ('ResourcesReady', 'WorkloadsSubmitted', 'RolloutReady', 'RetirementCompleted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints");

            migrationBuilder.AddCheckConstraint(
                name: "CK_generation_stage_checkpoints_identity",
                table: "generation_stage_checkpoints",
                sql: "generation > 0 AND format_version = 1 AND stage IN ('ResourcesReady', 'WorkloadsSubmitted', 'RolloutReady')");
        }
    }
}
