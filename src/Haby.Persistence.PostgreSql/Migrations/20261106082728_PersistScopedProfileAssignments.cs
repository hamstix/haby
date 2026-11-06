using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistScopedProfileAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configuration_profile_assignments",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    application_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    component_ref = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    token = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_application_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_revision_number = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_profile_assignments", x => new { x.environment_record_id, x.target_kind, x.application_id, x.component_ref });
                    table.CheckConstraint("CK_profile_assignments_audit", "length(actor_id) > 0 AND token <> '00000000-0000-0000-0000-000000000000'::uuid AND ((target_kind = 'component' AND checked_application_record_id IS NOT NULL AND checked_revision_number IS NOT NULL AND checked_revision_number > 0) OR (target_kind <> 'component' AND checked_application_record_id IS NULL AND checked_revision_number IS NULL))");
                    table.CheckConstraint("CK_profile_assignments_target", "(target_kind = 'environment' AND application_id = '' AND component_ref = '') OR (target_kind = 'application' AND application_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$' AND component_ref = '') OR (target_kind = 'component' AND application_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$' AND component_ref ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')");
                    table.ForeignKey(
                        name: "FK_configuration_profile_assignments_environments_environment_~",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_profile_assignments");
        }
    }
}
