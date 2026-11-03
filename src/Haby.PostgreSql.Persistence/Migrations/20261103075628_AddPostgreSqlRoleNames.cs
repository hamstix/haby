using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.PostgreSql.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostgreSqlRoleNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "postgresql_role_names",
                columns: table => new
                {
                    Provider = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<Guid>(type: "uuid", nullable: false),
                    Application = table.Column<Guid>(type: "uuid", nullable: false),
                    Resource = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Name = table.Column<string>(type: "character varying(55)", maxLength: 55, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_postgresql_role_names", x => new { x.Provider, x.Environment, x.Application, x.Resource });
                });

            migrationBuilder.CreateIndex(
                name: "IX_postgresql_role_names_Provider_Name",
                table: "postgresql_role_names",
                columns: new[] { "Provider", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "postgresql_role_names");
        }
    }
}
