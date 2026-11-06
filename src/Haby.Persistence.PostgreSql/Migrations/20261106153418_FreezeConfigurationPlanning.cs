using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class FreezeConfigurationPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM deployment_plans) OR EXISTS (SELECT 1 FROM application_deployment_generations) THEN
                    RAISE EXCEPTION 'Older durable planning inputs require an explicitly initialized fresh catalog; external resources remain unchanged.';
                  END IF;
                END $$;
                """);
            migrationBuilder.AddColumn<string>(
                name: "configuration_json",
                table: "deployment_plans",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "configuration_json",
                table: "application_deployment_generations",
                type: "text",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "configuration_json",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "configuration_json",
                table: "application_deployment_generations");
        }
    }
}
