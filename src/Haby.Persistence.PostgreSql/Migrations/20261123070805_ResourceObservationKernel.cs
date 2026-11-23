using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ResourceObservationKernel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management");

            migrationBuilder.DropIndex(
                name: "IX_resource_import_receipts_deployment_id_generation",
                table: "resource_import_receipts");

            migrationBuilder.AddColumn<string>(
                name: "group_id",
                table: "resource_import_receipts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "acquisition_intent_json",
                table: "deployment_change_sets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resource_identity_claims",
                columns: table => new
                {
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    association_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_identity_claims", x => new { x.provider_instance_id, x.@namespace, x.value });
                    table.ForeignKey(
                        name: "FK_resource_identity_claims_provider_instances_provider_instan~",
                        column: x => x.provider_instance_id,
                        principalTable: "provider_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_identity_claims_resource_management_association_id",
                        column: x => x.association_id,
                        principalTable: "resource_management",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management",
                sql: "source_generation > 0 AND length(resource_local_id) > 0 AND ((origin = 'Provisioned' AND authority = 'Provisioned') OR (origin = 'Imported' AND authority = 'UseAndRetain') OR (origin = 'Referenced' AND authority = 'ConsumptionOnly'))");

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_receipts_deployment_id_generation_group_id",
                table: "resource_import_receipts",
                columns: new[] { "deployment_id", "generation", "group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_identity_claims_association_id",
                table: "resource_identity_claims",
                column: "association_id");

            migrationBuilder.CreateIndex(
                name: "IX_resource_identity_claims_provider_instance_id_environment_r~",
                table: "resource_identity_claims",
                columns: new[] { "provider_instance_id", "environment_record_id", "application_record_id", "resource_local_id", "namespace" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_identity_claims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management");

            migrationBuilder.DropIndex(
                name: "IX_resource_import_receipts_deployment_id_generation_group_id",
                table: "resource_import_receipts");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "resource_import_receipts");

            migrationBuilder.DropColumn(
                name: "acquisition_intent_json",
                table: "deployment_change_sets");

            migrationBuilder.AddCheckConstraint(
                name: "CK_resource_management_identity",
                table: "resource_management",
                sql: "source_generation > 0 AND length(resource_local_id) > 0 AND ((origin = 'Provisioned' AND authority = 'Provisioned') OR (origin = 'Imported' AND authority = 'UseAndRetain'))");

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_receipts_deployment_id_generation",
                table: "resource_import_receipts",
                columns: new[] { "deployment_id", "generation" },
                unique: true);
        }
    }
}
