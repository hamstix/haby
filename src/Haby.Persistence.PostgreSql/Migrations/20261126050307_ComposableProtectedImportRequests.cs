using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ComposableProtectedImportRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_resource_import_supersessions_change_set_id",
                table: "resource_import_supersessions");

            migrationBuilder.DropIndex(
                name: "IX_operator_resource_import_requests_change_set_id",
                table: "operator_resource_import_requests");

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_change_set_id",
                table: "resource_import_supersessions",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_change_set_id",
                table: "operator_resource_import_requests",
                column: "change_set_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_resource_import_supersessions_change_set_id",
                table: "resource_import_supersessions");

            migrationBuilder.DropIndex(
                name: "IX_operator_resource_import_requests_change_set_id",
                table: "operator_resource_import_requests");

            migrationBuilder.CreateIndex(
                name: "IX_resource_import_supersessions_change_set_id",
                table: "resource_import_supersessions",
                column: "change_set_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operator_resource_import_requests_change_set_id",
                table: "operator_resource_import_requests",
                column: "change_set_id",
                unique: true);
        }
    }
}
