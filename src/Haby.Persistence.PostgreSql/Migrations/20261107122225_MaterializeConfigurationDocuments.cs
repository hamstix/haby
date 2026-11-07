using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MaterializeConfigurationDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configuration_document_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    component = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_document_revisions", x => x.id);
                    table.CheckConstraint("ck_configuration_document_generation", "generation > 0");
                    table.CheckConstraint("ck_configuration_document_payload_size", "octet_length(payload) <= 4194304");
                    table.ForeignKey(
                        name: "FK_configuration_document_revisions_application_deployment_gen~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_document_revisions_deployment_id_generation_c~",
                table: "configuration_document_revisions",
                columns: new[] { "deployment_id", "generation", "component", "document" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_document_revisions");
        }
    }
}
