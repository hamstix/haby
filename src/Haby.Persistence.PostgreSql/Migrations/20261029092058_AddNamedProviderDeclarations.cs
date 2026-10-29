using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddNamedProviderDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_instances_environment_record_id",
                table: "provider_instances");

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "provider_instances",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmation_actor",
                table: "provider_configuration_revisions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmed_at",
                table: "provider_configuration_revisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "confirmed_previous_revision",
                table: "provider_configuration_revisions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_instances_environment_record_id_name",
                table: "provider_instances",
                columns: new[] { "environment_record_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_instances_environment_record_id_name",
                table: "provider_instances");

            migrationBuilder.DropColumn(
                name: "name",
                table: "provider_instances");

            migrationBuilder.DropColumn(
                name: "confirmation_actor",
                table: "provider_configuration_revisions");

            migrationBuilder.DropColumn(
                name: "confirmed_at",
                table: "provider_configuration_revisions");

            migrationBuilder.DropColumn(
                name: "confirmed_previous_revision",
                table: "provider_configuration_revisions");

            migrationBuilder.CreateIndex(
                name: "IX_provider_instances_environment_record_id",
                table: "provider_instances",
                column: "environment_record_id");
        }
    }
}
