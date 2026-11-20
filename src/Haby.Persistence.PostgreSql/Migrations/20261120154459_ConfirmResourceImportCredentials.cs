using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmResourceImportCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM deployment_change_sets WHERE import_intent_json IS NOT NULL)
                     OR EXISTS (SELECT 1 FROM resource_management WHERE origin = 'Imported') THEN
                    RAISE EXCEPTION 'An explicit fresh catalog is required for protected import inputs and receipts.';
                  END IF;
                END $$;
                """);
            migrationBuilder.CreateTable(
                name: "resource_import_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_import_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_resource_import_receipts_application_deployment_generations~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "protected_application_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material = table.Column<byte[]>(type: "bytea", nullable: false),
                    confirmed_receipt_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protected_application_credentials", x => x.id);
                    table.CheckConstraint("CK_protected_application_credentials_material", "octet_length(material) BETWEEN 1 AND 65536");
                    table.ForeignKey(
                        name: "FK_protected_application_credentials_applications_application_~",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_protected_application_credentials_environments_environment_~",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_protected_application_credentials_resource_import_receipts_~",
                        column: x => x.confirmed_receipt_id,
                        principalTable: "resource_import_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_application_record_id",
                table: "protected_application_credentials",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_confirmed_receipt_id",
                table: "protected_application_credentials",
                column: "confirmed_receipt_id");

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_environment_record_id_app~",
                table: "protected_application_credentials",
                columns: new[] { "environment_record_id", "application_record_id", "resource_local_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_request_id",
                table: "protected_application_credentials",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_secret_id",
                table: "protected_application_credentials",
                column: "secret_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_version_id",
                table: "protected_application_credentials",
                column: "version_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_receipts_deployment_id_generation",
                table: "resource_import_receipts",
                columns: new[] { "deployment_id", "generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "protected_application_credentials");

            migrationBuilder.DropTable(
                name: "resource_import_receipts");
        }
    }
}
