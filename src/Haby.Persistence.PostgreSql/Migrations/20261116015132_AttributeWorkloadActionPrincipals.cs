using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AttributeWorkloadActionPrincipals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor",
                table: "durable_operation_observations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor",
                table: "durable_create_operation_attempts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor",
                table: "durable_action_intents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor",
                table: "durable_operation_observations");

            migrationBuilder.DropColumn(
                name: "actor",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropColumn(
                name: "actor",
                table: "durable_action_intents");
        }
    }
}
