using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    public partial class m006_Add_Organization_Units_table : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OrganizationUnitId",
                table: "ConfigurationUnits",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrganizationUnits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationUnits_OrganizationUnits_ParentId",
                        column: x => x.ParentId,
                        principalTable: "OrganizationUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationUnits_OrganizationUnitId",
                table: "ConfigurationUnits",
                column: "OrganizationUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationUnits_ParentId",
                table: "OrganizationUnits",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConfigurationUnits_OrganizationUnits_OrganizationUnitId",
                table: "ConfigurationUnits",
                column: "OrganizationUnitId",
                principalTable: "OrganizationUnits",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConfigurationUnits_OrganizationUnits_OrganizationUnitId",
                table: "ConfigurationUnits");

            migrationBuilder.DropTable(
                name: "OrganizationUnits");

            migrationBuilder.DropIndex(
                name: "IX_ConfigurationUnits_OrganizationUnitId",
                table: "ConfigurationUnits");

            migrationBuilder.DropColumn(
                name: "OrganizationUnitId",
                table: "ConfigurationUnits");
        }
    }
}
