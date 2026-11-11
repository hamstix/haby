using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPlacementRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "placement_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    format_version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_revisions", x => x.id);
                    table.CheckConstraint("CK_placement_actor", "length(actor_id) > 0");
                    table.CheckConstraint("CK_placement_alias", "provider_alias ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'");
                    table.CheckConstraint("CK_placement_name", "name ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'");
                    table.CheckConstraint("CK_placement_revision_positive", "revision > 0");
                    table.ForeignKey(
                        name: "FK_placement_revisions_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_placement_revision_exact",
                table: "placement_revisions",
                columns: new[] { "environment_record_id", "provider_alias", "name", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "placement_revisions");
        }
    }
}
