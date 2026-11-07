using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configuration_access_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    credential_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_access_grants", x => x.id);
                    table.CheckConstraint("ck_configuration_access_expiry", "expires_at IS NULL OR expires_at > created_at");
                    table.CheckConstraint("ck_configuration_access_hash", "octet_length(credential_hash) = 32");
                    table.CheckConstraint("ck_configuration_access_revoke", "(revoked_at IS NULL) = (revoked_by IS NULL)");
                    table.ForeignKey(
                        name: "FK_configuration_access_grants_configuration_document_revision~",
                        column: x => x.revision_id,
                        principalTable: "configuration_document_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_access_grants_revision_id",
                table: "configuration_access_grants",
                column: "revision_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_access_grants");
        }
    }
}
