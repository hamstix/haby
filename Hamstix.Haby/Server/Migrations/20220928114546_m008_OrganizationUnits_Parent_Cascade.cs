using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    public partial class m008_OrganizationUnits_Parent_Cascade : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationUnits_OrganizationUnits_ParentId",
                table: "OrganizationUnits");

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationUnits_OrganizationUnits_ParentId",
                table: "OrganizationUnits",
                column: "ParentId",
                principalTable: "OrganizationUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationUnits_OrganizationUnits_ParentId",
                table: "OrganizationUnits");

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationUnits_OrganizationUnits_ParentId",
                table: "OrganizationUnits",
                column: "ParentId",
                principalTable: "OrganizationUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
