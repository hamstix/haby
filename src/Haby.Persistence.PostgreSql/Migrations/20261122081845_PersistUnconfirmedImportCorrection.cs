using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistUnconfirmedImportCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_protected_application_credentials_environment_record_id_app~",
                table: "protected_application_credentials");

            migrationBuilder.AddColumn<string>(
                name: "correction_reason",
                table: "operator_resource_import_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "expected_deployment_id",
                table: "operator_resource_import_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "expected_generation",
                table: "operator_resource_import_requests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "previous_request_id",
                table: "operator_resource_import_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_protected_application_credentials_id_request_id_environment~",
                table: "protected_application_credentials",
                columns: new[] { "id", "request_id", "environment_record_id", "application_record_id", "resource_local_id" });

            migrationBuilder.CreateTable(
                name: "protected_application_credential_owners",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    staged_credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protected_application_credential_owners", x => new { x.environment_record_id, x.application_record_id, x.resource_local_id });
                    table.ForeignKey(
                        name: "FK_protected_application_credential_owners_protected_applicati~",
                        columns: x => new { x.staged_credential_id, x.request_id, x.environment_record_id, x.application_record_id, x.resource_local_id },
                        principalTable: "protected_application_credentials",
                        principalColumns: new[] { "id", "request_id", "environment_record_id", "application_record_id", "resource_local_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve every existing active owner without reading or rewriting credential material.
            migrationBuilder.Sql("""
                INSERT INTO protected_application_credential_owners
                    (environment_record_id, application_record_id, resource_local_id, staged_credential_id, request_id)
                SELECT environment_record_id, application_record_id, resource_local_id, id, request_id
                FROM protected_application_credentials;
                """);

            migrationBuilder.CreateTable(
                name: "resource_import_supersessions",
                columns: table => new
                {
                    previous_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    successor_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_staged_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    successor_staged_credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_import_supersessions", x => x.previous_request_id);
                    table.ForeignKey(
                        name: "FK_resource_import_supersessions_application_deployment_genera~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_import_supersessions_change_set_commits_change_set~",
                        column: x => x.change_set_id,
                        principalTable: "change_set_commits",
                        principalColumn: "change_set_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_import_supersessions_operator_resource_import_requ~",
                        column: x => x.previous_request_id,
                        principalTable: "operator_resource_import_requests",
                        principalColumn: "request_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_import_supersessions_operator_resource_import_req~1",
                        column: x => x.successor_request_id,
                        principalTable: "operator_resource_import_requests",
                        principalColumn: "request_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_import_supersessions_protected_application_credent~",
                        column: x => x.previous_staged_credential_id,
                        principalTable: "protected_application_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resource_import_supersessions_protected_application_creden~1",
                        column: x => x.successor_staged_credential_id,
                        principalTable: "protected_application_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_environment_record_id",
                table: "protected_application_credentials",
                column: "environment_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_expected_deployment_id_ex~",
                table: "operator_resource_import_requests",
                columns: new[] { "expected_deployment_id", "expected_generation" });

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_previous_request_id",
                table: "operator_resource_import_requests",
                column: "previous_request_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_operator_import_request_correction",
                table: "operator_resource_import_requests",
                sql: "(previous_request_id IS NULL AND expected_deployment_id IS NULL AND expected_generation IS NULL AND correction_reason IS NULL) OR (previous_request_id IS NOT NULL AND previous_request_id <> request_id AND correction_reason IS NOT NULL AND correction_reason IN ('CredentialRejected','InputCorrection','ExplicitRetry') AND ((expected_deployment_id IS NULL AND expected_generation IS NULL) OR (expected_deployment_id IS NOT NULL AND expected_generation IS NOT NULL AND expected_generation > 0)))");

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credential_owners_staged_credential_i~",
                table: "protected_application_credential_owners",
                columns: new[] { "staged_credential_id", "request_id", "environment_record_id", "application_record_id", "resource_local_id" });

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credential_owners_staged_credential_id",
                table: "protected_application_credential_owners",
                column: "staged_credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_change_set_id",
                table: "resource_import_supersessions",
                column: "change_set_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_deployment_id_generation",
                table: "resource_import_supersessions",
                columns: new[] { "deployment_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_previous_staged_credential_id",
                table: "resource_import_supersessions",
                column: "previous_staged_credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_successor_request_id",
                table: "resource_import_supersessions",
                column: "successor_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_successor_staged_credential_id",
                table: "resource_import_supersessions",
                column: "successor_staged_credential_id");

            migrationBuilder.AddForeignKey(
                name: "FK_operator_resource_import_requests_application_deployment_ge~",
                table: "operator_resource_import_requests",
                columns: new[] { "expected_deployment_id", "expected_generation" },
                principalTable: "application_deployment_generations",
                principalColumns: new[] { "deployment_id", "generation" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_operator_resource_import_requests_operator_resource_import_~",
                table: "operator_resource_import_requests",
                column: "previous_request_id",
                principalTable: "operator_resource_import_requests",
                principalColumn: "request_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropIndex(
                name: "IX_resource_management_environment_record_id_application_recor~",
                table: "resource_management");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_resource_management_id_environment_record_id_application_re~",
                table: "resource_management",
                columns: new[] { "id", "environment_record_id", "application_record_id", "resource_local_id" });

            migrationBuilder.CreateTable(
                name: "resource_management_heads",
                columns: table => new
                {
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    management_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_management_heads", x => new { x.environment_record_id, x.application_record_id, x.resource_local_id });
                    table.ForeignKey(
                        name: "FK_resource_management_heads_resource_management_management_id~",
                        columns: x => new { x.management_id, x.environment_record_id, x.application_record_id, x.resource_local_id },
                        principalTable: "resource_management",
                        principalColumns: new[] { "id", "environment_record_id", "application_record_id", "resource_local_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_environment_record_id_application_recor~",
                table: "resource_management",
                columns: new[] { "environment_record_id", "application_record_id", "resource_local_id", "source_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_heads_management_id",
                table: "resource_management_heads",
                column: "management_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_heads_management_id_environment_record_~",
                table: "resource_management_heads",
                columns: new[] { "management_id", "environment_record_id", "application_record_id", "resource_local_id" });
            migrationBuilder.Sql("""
                INSERT INTO resource_management_heads (environment_record_id, application_record_id, resource_local_id, management_id)
                SELECT environment_record_id, application_record_id, resource_local_id, id FROM resource_management;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM operator_resource_import_requests WHERE previous_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM resource_import_supersessions) THEN
                    RAISE EXCEPTION 'Import correction history requires the current catalog schema; downgrade is refused.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_operator_resource_import_requests_application_deployment_ge~",
                table: "operator_resource_import_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_operator_resource_import_requests_operator_resource_import_~",
                table: "operator_resource_import_requests");

            migrationBuilder.DropTable(
                name: "protected_application_credential_owners");

            migrationBuilder.DropTable(
                name: "resource_import_supersessions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_protected_application_credentials_id_request_id_environment~",
                table: "protected_application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_protected_application_credentials_environment_record_id",
                table: "protected_application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_operator_resource_import_requests_expected_deployment_id_ex~",
                table: "operator_resource_import_requests");

            migrationBuilder.DropIndex(
                name: "IX_operator_resource_import_requests_previous_request_id",
                table: "operator_resource_import_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_operator_import_request_correction",
                table: "operator_resource_import_requests");

            migrationBuilder.DropColumn(
                name: "correction_reason",
                table: "operator_resource_import_requests");

            migrationBuilder.DropColumn(
                name: "expected_deployment_id",
                table: "operator_resource_import_requests");

            migrationBuilder.DropColumn(
                name: "expected_generation",
                table: "operator_resource_import_requests");

            migrationBuilder.DropColumn(
                name: "previous_request_id",
                table: "operator_resource_import_requests");

            migrationBuilder.CreateIndex(
                name: "IX_protected_application_credentials_environment_record_id_app~",
                table: "protected_application_credentials",
                columns: new[] { "environment_record_id", "application_record_id", "resource_local_id" },
                unique: true);

            migrationBuilder.DropTable(
                name: "resource_management_heads");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_resource_management_id_environment_record_id_application_re~",
                table: "resource_management");

            migrationBuilder.DropIndex(
                name: "IX_resource_management_environment_record_id_application_recor~",
                table: "resource_management");

            migrationBuilder.CreateIndex(
                name: "IX_resource_management_environment_record_id_application_recor~",
                table: "resource_management",
                columns: new[] { "environment_record_id", "application_record_id", "resource_local_id" },
                unique: true);

        }
    }
}
