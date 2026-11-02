using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistGeneratedCredentialOutputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operation_outputs",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    output_contract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    output_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_outputs", x => new { x.deployment_id, x.generation, x.operation_id, x.output_contract, x.output_key });
                    table.CheckConstraint("CK_operation_outputs_contract", "length(output_contract) > 0 AND length(output_key) > 0");
                    table.ForeignKey(
                        name: "FK_operation_outputs_durable_create_operations_deployment_id_g~",
                        columns: x => new { x.deployment_id, x.generation, x.operation_id },
                        principalTable: "durable_create_operations",
                        principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_outputs");
        }
    }
}
