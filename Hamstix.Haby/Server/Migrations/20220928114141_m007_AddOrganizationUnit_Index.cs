using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    public partial class m007_AddOrganizationUnit_Index : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_Name_ParentId",
                table: "OrganizationUnits",
                columns: new[] { "Name", "ParentId" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrganizationUnits_Name_ParentId",
                table: "OrganizationUnits");
        }
    }
}
