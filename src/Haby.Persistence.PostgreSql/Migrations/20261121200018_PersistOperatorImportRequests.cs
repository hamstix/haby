using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistOperatorImportRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operator_resource_import_requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    credential_resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    input = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    staged_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_resource_import_requests", x => x.request_id);
                    table.CheckConstraint("CK_operator_import_request_completion", "(change_set_id IS NULL AND staged_credential_id IS NULL AND completed_at IS NULL AND completed_by IS NULL) OR (change_set_id IS NOT NULL AND staged_credential_id IS NOT NULL AND completed_at IS NOT NULL AND completed_by IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_operator_resource_import_requests_applications_application_~",
                        column: x => x.application_record_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operator_resource_import_requests_deployment_change_sets_ch~",
                        column: x => x.change_set_id,
                        principalTable: "deployment_change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operator_resource_import_requests_environments_environment_~",
                        column: x => x.environment_record_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operator_resource_import_requests_manifest_revisions_manife~",
                        column: x => x.manifest_revision_id,
                        principalTable: "manifest_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_operator_resource_import_requests_protected_application_cre~",
                        column: x => x.staged_credential_id,
                        principalTable: "protected_application_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_application_record_id",
                table: "operator_resource_import_requests",
                column: "application_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_change_set_id",
                table: "operator_resource_import_requests",
                column: "change_set_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_environment_record_id",
                table: "operator_resource_import_requests",
                column: "environment_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_manifest_revision_id",
                table: "operator_resource_import_requests",
                column: "manifest_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_staged_credential_id",
                table: "operator_resource_import_requests",
                column: "staged_credential_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operator_resource_import_requests");
        }
    }
}
