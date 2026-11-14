using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeConfigurationPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_runtime_binding",
                table: "runtime_identity_bindings");

            migrationBuilder.AddColumn<Guid>(
                name: "concurrency_token",
                table: "runtime_identity_bindings",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            // Initialize only lifecycle tokens for existing bindings; origin evidence is untouched.
            migrationBuilder.AlterColumn<Guid>(
                name: "concurrency_token", table: "runtime_identity_bindings", type: "uuid", nullable: false,
                oldClrType: typeof(Guid), oldType: "uuid", oldDefaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                table: "runtime_identity_bindings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revoked_by",
                table: "runtime_identity_bindings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "runtime_publication_receipts",
                columns: table => new
                {
                    request_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_publication_receipts", x => x.request_key);
                    table.CheckConstraint("CK_runtime_publication_receipt", "length(request_key) BETWEEN 1 AND 128 AND octet_length(payload) BETWEEN 1 AND 16384");
                    table.ForeignKey(
                        name: "FK_runtime_publication_receipts_configuration_document_revisio~",
                        column: x => x.revision_id,
                        principalTable: "configuration_document_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_runtime_publication_receipts_runtime_identity_bindings_bind~",
                        column: x => x.binding_id,
                        principalTable: "runtime_identity_bindings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "runtime_configuration_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    binding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    concurrency_token = table.Column<Guid>(type: "uuid", nullable: false),
                    origin_payload = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_request = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_configuration_permissions", x => x.id);
                    table.CheckConstraint("CK_runtime_permission", "status IN ('Published', 'Revoked') AND concurrency_token <> '00000000-0000-0000-0000-000000000000'::uuid AND octet_length(origin_payload) BETWEEN 1 AND 16384");
                    table.ForeignKey(
                        name: "FK_runtime_configuration_permissions_configuration_document_re~",
                        column: x => x.revision_id,
                        principalTable: "configuration_document_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_runtime_configuration_permissions_runtime_identity_bindings~",
                        column: x => x.binding_id,
                        principalTable: "runtime_identity_bindings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_runtime_configuration_permissions_runtime_publication_recei~",
                        column: x => x.last_request,
                        principalTable: "runtime_publication_receipts",
                        principalColumn: "request_key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_runtime_binding",
                table: "runtime_identity_bindings",
                sql: "format > 0 AND status IN ('Prepared', 'Revoked') AND concurrency_token <> '00000000-0000-0000-0000-000000000000'::uuid AND octet_length(validation_payload) BETWEEN 1 AND 1048576 AND ((status = 'Prepared' AND revoked_at IS NULL AND revoked_by IS NULL) OR (status = 'Revoked' AND revoked_at IS NOT NULL AND revoked_by IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_configuration_permissions_binding_id_revision_id",
                table: "runtime_configuration_permissions",
                columns: new[] { "binding_id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_configuration_permissions_last_request",
                table: "runtime_configuration_permissions",
                column: "last_request");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_configuration_permissions_revision_id",
                table: "runtime_configuration_permissions",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_publication_receipts_binding_id",
                table: "runtime_publication_receipts",
                column: "binding_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_publication_receipts_id",
                table: "runtime_publication_receipts",
                column: "id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_publication_receipts_revision_id",
                table: "runtime_publication_receipts",
                column: "revision_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_configuration_permissions");

            migrationBuilder.DropTable(
                name: "runtime_publication_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_runtime_binding",
                table: "runtime_identity_bindings");

            migrationBuilder.DropColumn(
                name: "concurrency_token",
                table: "runtime_identity_bindings");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "runtime_identity_bindings");

            migrationBuilder.DropColumn(
                name: "revoked_by",
                table: "runtime_identity_bindings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_runtime_binding",
                table: "runtime_identity_bindings",
                sql: "format > 0 AND status = 'Prepared' AND octet_length(validation_payload) BETWEEN 1 AND 1048576");
        }
    }
}
