using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    public partial class m002_Change_ConfigurationKey_PK : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ConfigurationKeys",
                table: "ConfigurationKeys");

            migrationBuilder.DropIndex(
                name: "IX_ConfigurationKeys_ConfigurationUnitId",
                table: "ConfigurationKeys");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConfigurationKeys",
                table: "ConfigurationKeys",
                columns: new[] { "ConfigurationUnitId", "Name" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ConfigurationKeys",
                table: "ConfigurationKeys");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ConfigurationKeys",
                table: "ConfigurationKeys",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationKeys_ConfigurationUnitId",
                table: "ConfigurationKeys",
                column: "ConfigurationUnitId");
        }
    }
}
