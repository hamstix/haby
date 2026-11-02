using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderNeutralOperationReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations",
                sql: "reason_code IS NULL OR reason_code IN ('ProviderInputUnavailable', 'SecretUnavailable', 'UnsupportedShape', 'DatabaseNameInvalid', 'Connectivity', 'Conflict', 'ServerRejected', 'UncertainAfterDispatch', 'InputInvalid', 'InputUnavailable', 'HandlerUnavailable')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL validates the legacy constraint against every retained row. New reasons
            // make ADD CONSTRAINT fail; EF's migration transaction restores the current constraint
            // and migration history atomically. Never delete or relabel rows to permit rollback.
            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations",
                sql: "reason_code IS NULL OR reason_code IN ('ProviderInputUnavailable', 'SecretUnavailable', 'UnsupportedShape', 'DatabaseNameInvalid', 'Connectivity', 'Conflict', 'ServerRejected', 'UncertainAfterDispatch')");
        }
    }
}
