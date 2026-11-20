using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistResourceImportAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM deployment_plans)
                     OR EXISTS (SELECT 1 FROM application_deployment_generations) THEN
                    RAISE EXCEPTION 'An explicit fresh catalog is required for Resource import provenance and durable formats.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management");

            migrationBuilder.AddColumn<string>(
                name: "authority",
                table: "resource_management",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "resource_management",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "import_intent_json",
                table: "deployment_change_sets",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management",
                sql: "source_generation > 0 AND length(resource_local_id) > 0 AND ((origin = 'Provisioned' AND authority = 'Provisioned') OR (origin = 'Imported' AND authority = 'UseAndRetain'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management");

            migrationBuilder.DropColumn(
                name: "authority",
                table: "resource_management");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "resource_management");

            migrationBuilder.DropColumn(
                name: "import_intent_json",
                table: "deployment_change_sets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management",
                sql: "source_generation > 0 AND length(resource_local_id) > 0");
        }
    }
}
