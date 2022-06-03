using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    public partial class m001_Initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigurationUnits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PreviousVersion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Template = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegConfiguration",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegConfiguration", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Services",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    JsonConfig = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Template = table.Column<string>(type: "text", nullable: true),
                    PluginName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationKeys",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ConfigurationUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationKeys", x => x.Name);
                    table.ForeignKey(
                        name: "FK_ConfigurationKeys_ConfigurationUnits_ConfigurationUnitId",
                        column: x => x.ConfigurationUnitId,
                        principalTable: "ConfigurationUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationUnitsAtServices",
                columns: table => new
                {
                    ServiceId = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    RenderedTemplateJson = table.Column<string>(type: "jsonb", nullable: true, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationUnitsAtServices", x => new { x.ServiceId, x.ConfigurationUnitId, x.Key });
                    table.ForeignKey(
                        name: "FK_ConfigurationUnitsAtServices_ConfigurationUnits_Configurati~",
                        column: x => x.ConfigurationUnitId,
                        principalTable: "ConfigurationUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConfigurationUnitsAtServices_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Variables",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    ServiceId = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Variables", x => new { x.ServiceId, x.ConfigurationUnitId, x.Key, x.Name });
                    table.ForeignKey(
                        name: "FK_Variables_ConfigurationUnitsAtServices_ServiceId_Configurat~",
                        columns: x => new { x.ServiceId, x.ConfigurationUnitId, x.Key },
                        principalTable: "ConfigurationUnitsAtServices",
                        principalColumns: new[] { "ServiceId", "ConfigurationUnitId", "Key" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "RegConfiguration",
                columns: new[] { "Key", "Value" },
                values: new object[] { "initialized", "false" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationKeys_ConfigurationUnitId",
                table: "ConfigurationKeys",
                column: "ConfigurationUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationUnitsAtServices_ConfigurationUnitId",
                table: "ConfigurationUnitsAtServices",
                column: "ConfigurationUnitId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationKeys");

            migrationBuilder.DropTable(
                name: "RegConfiguration");

            migrationBuilder.DropTable(
                name: "Variables");

            migrationBuilder.DropTable(
                name: "ConfigurationUnitsAtServices");

            migrationBuilder.DropTable(
                name: "ConfigurationUnits");

            migrationBuilder.DropTable(
                name: "Services");
        }
    }
}
