using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceSharingAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_resource_identity_claims",
                table: "resource_identity_claims");

            migrationBuilder.AddColumn<string>(
                name: "sharing_json",
                table: "resource_identity_claims",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_resource_identity_claims",
                table: "resource_identity_claims",
                columns: new[] { "provider_instance_id", "namespace", "value", "association_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_resource_identity_claims",
                table: "resource_identity_claims");

            migrationBuilder.DropColumn(
                name: "sharing_json",
                table: "resource_identity_claims");

            migrationBuilder.AddPrimaryKey(
                name: "PK_resource_identity_claims",
                table: "resource_identity_claims",
                columns: new[] { "provider_instance_id", "namespace", "value" });
        }
    }
}
