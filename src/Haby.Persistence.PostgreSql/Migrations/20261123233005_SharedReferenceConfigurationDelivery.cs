using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class SharedReferenceConfigurationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shared_credential_bindings_provider_instance_id_revision_nu~",
                table: "shared_credential_bindings");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_shared_credential_bindings_provider_instance_id_revision_nu~",
                table: "shared_credential_bindings",
                columns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id", "use" });

            migrationBuilder.CreateTable(
                name: "configuration_shared_credential_uses",
                columns: table => new
                {
                    document_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    projection_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    projection = table.Column<string>(type: "text", nullable: false),
                    environment_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_local_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    association_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<long>(type: "bigint", nullable: false),
                    slot = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    use = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    producer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    formatter = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    formatter_implementation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_shared_credential_uses", x => new { x.document_revision_id, x.projection_hash });
                    table.CheckConstraint("ck_configuration_shared_use", "use = 'ApplicationDelivery' AND revision_number > 0");
                    table.ForeignKey(
                        name: "FK_configuration_shared_credential_uses_configuration_document~",
                        column: x => x.document_revision_id,
                        principalTable: "configuration_document_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_configuration_shared_credential_uses_durable_create_operati~",
                        column: x => x.attempt_id,
                        principalTable: "durable_create_operation_attempts",
                        principalColumn: "attempt_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_configuration_shared_credential_uses_resource_management_as~",
                        columns: x => new { x.association_id, x.environment_record_id, x.application_record_id, x.resource_local_id },
                        principalTable: "resource_management",
                        principalColumns: new[] { "id", "environment_record_id", "application_record_id", "resource_local_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_configuration_shared_credential_uses_shared_credential_bind~",
                        columns: x => new { x.provider_instance_id, x.revision_number, x.slot, x.environment_record_id, x.secret_id, x.secret_version_id, x.use },
                        principalTable: "shared_credential_bindings",
                        principalColumns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id", "use" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_shared_credential_uses_association_id_environ~",
                table: "configuration_shared_credential_uses",
                columns: new[] { "association_id", "environment_record_id", "application_record_id", "resource_local_id" });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_shared_credential_uses_attempt_id",
                table: "configuration_shared_credential_uses",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_configuration_shared_credential_uses_provider_instance_id_~1",
                table: "configuration_shared_credential_uses",
                columns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id", "use" });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_shared_credential_uses_provider_instance_id_r~",
                table: "configuration_shared_credential_uses",
                columns: new[] { "provider_instance_id", "revision_number", "slot" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_shared_credential_uses");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_shared_credential_bindings_provider_instance_id_revision_nu~",
                table: "shared_credential_bindings");

            migrationBuilder.CreateIndex(
                name: "IX_shared_credential_bindings_provider_instance_id_revision_nu~",
                table: "shared_credential_bindings",
                columns: new[] { "provider_instance_id", "revision_number", "slot", "environment_record_id", "secret_id", "secret_version_id" });
        }
    }
}
