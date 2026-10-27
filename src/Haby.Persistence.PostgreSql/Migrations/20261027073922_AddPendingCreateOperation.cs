using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingCreateOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "durable_create_operations",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_create_operations", x => new { x.deployment_id, x.generation, x.resource_id, x.action });
                    table.CheckConstraint("CK_durable_create_operations_action", "action = 'Create'");
                    table.CheckConstraint("CK_durable_create_operations_resource_id", "length(resource_id) > 0");
                    table.CheckConstraint("CK_durable_create_operations_state", "state = 'Pending'");
                    table.ForeignKey(
                        name: "FK_durable_create_operations_application_deployment_generation~",
                        columns: x => new { x.deployment_id, x.generation },
                        principalTable: "application_deployment_generations",
                        principalColumns: new[] { "deployment_id", "generation" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                CREATE TRIGGER durable_create_operations_before_delete BEFORE DELETE ON durable_create_operations
                    FOR EACH ROW EXECUTE FUNCTION reject_deployment_planning_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER durable_create_operations_before_delete ON durable_create_operations;");
            migrationBuilder.DropTable(
                name: "durable_create_operations");
        }
    }
}
