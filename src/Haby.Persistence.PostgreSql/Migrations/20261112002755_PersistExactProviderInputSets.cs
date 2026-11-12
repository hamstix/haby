using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistExactProviderInputSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE deployment_plans, application_deployment_generations IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM deployment_plans) OR EXISTS (SELECT 1 FROM application_deployment_generations) THEN
                        RAISE EXCEPTION 'Incompatible durable input format; explicitly initialize a fresh catalog. External resources remain unchanged.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_application_deployment_generations_provider_configuration_r~",
                table: "application_deployment_generations");

            migrationBuilder.DropForeignKey(
                name: "FK_deployment_plans_provider_configuration_revisions_provider_~",
                table: "deployment_plans");

            migrationBuilder.DropIndex(
                name: "IX_deployment_plans_provider_instance_id_configuration_revisio~",
                table: "deployment_plans");

            migrationBuilder.DropIndex(
                name: "IX_application_deployment_generations_provider_instance_id_con~",
                table: "application_deployment_generations");

            migrationBuilder.DropColumn(
                name: "configuration_revision_number",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "implementation_id",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "provider_alias",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "provider_instance_id",
                table: "deployment_plans");

            migrationBuilder.DropColumn(
                name: "configuration_revision_number",
                table: "application_deployment_generations");

            migrationBuilder.DropColumn(
                name: "implementation_id",
                table: "application_deployment_generations");

            migrationBuilder.DropColumn(
                name: "provider_alias",
                table: "application_deployment_generations");

            migrationBuilder.DropColumn(
                name: "provider_instance_id",
                table: "application_deployment_generations");

            migrationBuilder.DropColumn(name: "naming_json", table: "deployment_plans");
            migrationBuilder.AddColumn<string>(name: "provider_inputs_json", table: "deployment_plans", type: "text", nullable: false);

            migrationBuilder.DropColumn(name: "naming_json", table: "application_deployment_generations");
            migrationBuilder.AddColumn<string>(name: "provider_inputs_json", table: "application_deployment_generations", type: "text", nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "snapshot_format_version",
                table: "application_deployment_generations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE deployment_plans, application_deployment_generations IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM deployment_plans) OR EXISTS (SELECT 1 FROM application_deployment_generations) THEN
                        RAISE EXCEPTION 'Incompatible durable input format; explicitly initialize a fresh catalog. External resources remain unchanged.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "snapshot_format_version",
                table: "application_deployment_generations");

            migrationBuilder.RenameColumn(
                name: "provider_inputs_json",
                table: "deployment_plans",
                newName: "naming_json");

            migrationBuilder.RenameColumn(
                name: "provider_inputs_json",
                table: "application_deployment_generations",
                newName: "naming_json");

            migrationBuilder.AddColumn<long>(
                name: "configuration_revision_number",
                table: "deployment_plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "implementation_id",
                table: "deployment_plans",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "provider_alias",
                table: "deployment_plans",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "provider_instance_id",
                table: "deployment_plans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "configuration_revision_number",
                table: "application_deployment_generations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "implementation_id",
                table: "application_deployment_generations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "provider_alias",
                table: "application_deployment_generations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "provider_instance_id",
                table: "application_deployment_generations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_deployment_plans_provider_instance_id_configuration_revisio~",
                table: "deployment_plans",
                columns: new[] { "provider_instance_id", "configuration_revision_number", "environment_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_deployment_generations_provider_instance_id_con~",
                table: "application_deployment_generations",
                columns: new[] { "provider_instance_id", "configuration_revision_number", "environment_record_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_application_deployment_generations_provider_configuration_r~",
                table: "application_deployment_generations",
                columns: new[] { "provider_instance_id", "configuration_revision_number", "environment_record_id" },
                principalTable: "provider_configuration_revisions",
                principalColumns: new[] { "provider_instance_id", "revision_number", "environment_record_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_deployment_plans_provider_configuration_revisions_provider_~",
                table: "deployment_plans",
                columns: new[] { "provider_instance_id", "configuration_revision_number", "environment_record_id" },
                principalTable: "provider_configuration_revisions",
                principalColumns: new[] { "provider_instance_id", "revision_number", "environment_record_id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
