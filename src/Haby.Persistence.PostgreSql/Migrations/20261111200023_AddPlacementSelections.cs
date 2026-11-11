using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPlacementSelections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_placement_revisions_id_environment_record_id_provider_alias",
                table: "placement_revisions",
                columns: new[] { "id", "environment_record_id", "provider_alias" });

            migrationBuilder.CreateTable(
                name: "placement_selections",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    selector = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    application_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    component_ref = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_application_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_revision_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_selections", x => new { x.environment_record_id, x.provider_alias, x.target_kind, x.selector, x.application_id, x.component_ref });
                    table.CheckConstraint("CK_placement_selection_audit", "length(actor_id) > 0 AND token <> '00000000-0000-0000-0000-000000000000'::uuid AND ((target_kind = 'component' AND checked_application_record_id IS NOT NULL AND checked_revision_number > 0 AND checked_revision_number IS NOT NULL) OR (target_kind <> 'component' AND checked_application_record_id IS NULL AND checked_revision_number IS NULL))");
                    table.CheckConstraint("CK_placement_selection_target", "(target_kind = 'named' AND selector ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$' AND application_id = '' AND component_ref = '') OR (target_kind = 'environment' AND selector = '' AND application_id = '' AND component_ref = '') OR (target_kind = 'application' AND selector = '' AND application_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$' AND component_ref = '') OR (target_kind = 'component' AND selector = '' AND application_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$' AND component_ref ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')");
                    table.ForeignKey(
                        name: "FK_placement_selections_placement_revisions_revision_id_enviro~",
                        columns: x => new { x.revision_id, x.environment_record_id, x.provider_alias },
                        principalTable: "placement_revisions",
                        principalColumns: new[] { "id", "environment_record_id", "provider_alias" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_placement_selections_revision_id_environment_record_id_prov~",
                table: "placement_selections",
                columns: new[] { "revision_id", "environment_record_id", "provider_alias" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "placement_selections");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_placement_revisions_id_environment_record_id_provider_alias",
                table: "placement_revisions");
        }
    }
}
