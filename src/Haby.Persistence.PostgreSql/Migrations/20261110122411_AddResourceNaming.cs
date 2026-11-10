using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "naming_json",
                table: "deployment_plans",
                type: "text",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "naming_json",
                table: "application_deployment_generations",
                type: "text",
                nullable: false);

            migrationBuilder.CreateTable(
                name: "resource_naming_policies",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_naming_policies", x => new { x.environment_record_id, x.name, x.revision });
                    table.CheckConstraint("CK_resource_naming_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_resource_naming_policies_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "resource_naming_selections",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    token = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_naming_selections", x => new { x.environment_record_id, x.alias });
                    table.ForeignKey(
                        name: "FK_resource_naming_selections_provider_aliases_environment_rec~",
                        columns: x => new { x.environment_record_id, x.alias },
                        principalTable: "provider_aliases",
                        principalColumns: new[] { "environment_record_id", "alias" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_naming_selections_resource_naming_policies_environ~",
                        columns: x => new { x.environment_record_id, x.name, x.revision },
                        principalTable: "resource_naming_policies",
                        principalColumns: new[] { "environment_record_id", "name", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_resource_naming_selections_environment_record_id_name_revis~",
                table: "resource_naming_selections",
                columns: new[] { "environment_record_id", "name", "revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_naming_selections");

            migrationBuilder.DropTable(
                name: "resource_naming_policies");

            migrationBuilder.DropColumn(
                name: "naming_json",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "naming_json",
                table: "application_deployment_generations");
        }
    }
}
