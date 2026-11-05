using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistConfigurationProfileRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configuration_profile_revisions",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    model_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot_format_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_profile_revisions", x => new { x.profile_id, x.revision });
                    table.CheckConstraint("CK_profile_revisions_actor", "length(actor_id) > 0");
                    table.CheckConstraint("CK_profile_revisions_id", "profile_id ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'");
                    table.CheckConstraint("CK_profile_revisions_revision", "revision > 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_profile_revisions");
        }
    }
}
