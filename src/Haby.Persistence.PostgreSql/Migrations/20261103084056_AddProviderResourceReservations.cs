using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderResourceReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_resource_reservations",
                columns: table => new
                {
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_resource_reservations", x => new { x.provider_instance_id, x.environment_record_id, x.application_record_id, x.resource_local_id, x.@namespace });
                    table.ForeignKey(
                        name: "FK_provider_resource_reservations_applications_application_rec~",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_resource_reservations_provider_instances_provider_~",
                        columns: x => new { x.provider_instance_id, x.environment_record_id },
                        principalTable: "provider_instances",
                        principalColumns: new[] { "id", "environment_record_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_resource_reservations_application_record_id",
                table: "provider_resource_reservations",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_resource_reservations_provider_instance_id_namespa~",
                table: "provider_resource_reservations",
                columns: new[] { "provider_instance_id", "namespace", "value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_resource_reservations");
        }
    }
}
