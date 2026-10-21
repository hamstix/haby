using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class InitialManifestCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "manifest_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    application_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    local_revision_number = table.Column<long>(type: "bigint", nullable: false),
                    input_api_version = table.Column<string>(type: "text", nullable: false),
                    normalized_model_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot_format_version = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    historical_warnings = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manifest_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_manifest_revisions_applications_application_record_id",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_application_id",
                table: "applications",
                column: "application_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manifest_revisions_application_id_application_version",
                table: "manifest_revisions",
                columns: new[] { "application_id", "application_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manifest_revisions_application_record_id_local_revision_num~",
                table: "manifest_revisions",
                columns: new[] { "application_record_id", "local_revision_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manifest_revisions");

            migrationBuilder.DropTable(
                name: "applications");
        }
    }
}
