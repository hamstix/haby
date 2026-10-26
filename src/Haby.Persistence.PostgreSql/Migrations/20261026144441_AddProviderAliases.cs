using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_alias_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    previous_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_revision_number = table.Column<long>(type: "bigint", nullable: true),
                    new_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    new_revision_number = table.Column<long>(type: "bigint", nullable: false),
                    technical_actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_alias_audit", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_alias_audit_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_aliases",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_aliases", x => new { x.environment_record_id, x.alias });
                    table.ForeignKey(
                        name: "FK_provider_aliases_environments_environment_record_id",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_aliases_provider_configuration_revisions_provider_~",
                        columns: x => new { x.provider_instance_id, x.revision_number, x.environment_record_id },
                        principalTable: "provider_configuration_revisions",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_alias_audit_environment_record_id",
                table: "provider_alias_audit",
                column: "environment_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_aliases_provider_instance_id",
                table: "provider_aliases",
                column: "provider_instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_aliases_provider_instance_id_revision_number_envir~",
                table: "provider_aliases",
                columns: new[] { "provider_instance_id", "revision_number", "environment_record_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_alias_audit");

            migrationBuilder.DropTable(
                name: "provider_aliases");
        }
    }
}
