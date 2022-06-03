using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    public partial class m005_Add_Parameters_Table : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "RegConfiguration",
                keyColumn: "Key",
                keyValue: "initialized");

            migrationBuilder.CreateTable(
                name: "ConfigurationUnitParameters",
                columns: table => new
                {
                    ConfigurationUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationUnitParameters", x => new { x.ConfigurationUnitId, x.Name, x.Key });
                    table.ForeignKey(
                        name: "FK_ConfigurationUnitParameters_ConfigurationUnits_Configuratio~",
                        column: x => x.ConfigurationUnitId,
                        principalTable: "ConfigurationUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationUnitParameters");

            migrationBuilder.InsertData(
                table: "RegConfiguration",
                columns: new[] { "Key", "Value" },
                values: new object[] { "initialized", "false" });
        }
    }
}
