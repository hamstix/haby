using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PinConfigurationRendering : Migration
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

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
