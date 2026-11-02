using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class PersistOperationGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only a fresh catalog may reach this development revision. No historical graph
            // is synthesized, and no previous effect/history is migrated or reset.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM deployment_plans) OR EXISTS (SELECT 1 FROM application_deployments) THEN
                    RAISE EXCEPTION 'A fresh catalog is required for the operation graph model.';
                  END IF;
                END $$;
                ALTER TABLE postgresql_database_recovery_decisions DROP CONSTRAINT FK_postgresql_recovery_committed_operation;
                ALTER TABLE postgresql_database_recovery_decisions DROP CONSTRAINT FK_postgresql_recovery_uncertain_attempt;
                DROP TRIGGER durable_create_operations_before_mutation ON durable_create_operations;
                DROP TRIGGER durable_create_operation_attempts_before_mutation ON durable_create_operation_attempts;
                DROP FUNCTION guard_durable_create_operation_mutation();
                DROP FUNCTION guard_durable_create_operation_attempt_mutation();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_durable_create_operation_attempts_durable_create_operations~",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropForeignKey(
                name: "FK_postgresql_database_recovery_decisions_durable_create_oper~1",
                table: "postgresql_database_recovery_decisions");

            migrationBuilder.DropIndex(
                name: "IX_postgresql_database_recovery_decisions_deployment_id_genera~",
                table: "postgresql_database_recovery_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_postgresql_recovery_committed_state",
                table: "postgresql_database_recovery_decisions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_durable_create_operations",
                table: "durable_create_operations");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operations_deployment_id_generation_resourc~1",
                table: "durable_create_operations");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operations_deployment_id_generation_resource~",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_action",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_resource_id",
                table: "durable_create_operations");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operation_attempts_attempt_id_deployment_id_~",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operation_attempts_deployment_id_generation_~",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operation_attempts_deployment_id_generation~1",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operation_attempts_action",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropColumn(
                name: "action",
                table: "postgresql_database_recovery_decisions");

            migrationBuilder.DropColumn(
                name: "action",
                table: "durable_create_operations");

            migrationBuilder.DropColumn(
                name: "action",
                table: "durable_create_operation_attempts");

            migrationBuilder.RenameColumn(
                name: "resource_id",
                table: "postgresql_database_recovery_decisions",
                newName: "operation_id");

            migrationBuilder.RenameColumn(
                name: "resource_id",
                table: "durable_create_operations",
                newName: "operation_id");

            migrationBuilder.RenameColumn(
                name: "resource_id",
                table: "durable_create_operation_attempts",
                newName: "operation_id");

            migrationBuilder.AlterColumn<long>(
                name: "applied_generation",
                table: "postgresql_database_recovery_decisions",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "graph_json",
                table: "deployment_plans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "graph_json",
                table: "application_deployment_generations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_durable_create_operations",
                table: "durable_create_operations",
                columns: new[] { "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_postgresql_database_recovery_decisions_deployment_id_genera~",
                table: "postgresql_database_recovery_decisions",
                columns: new[] { "deployment_id", "generation", "operation_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_postgresql_recovery_committed_state",
                table: "postgresql_database_recovery_decisions",
                sql: "operation_state = 'Succeeded' AND (applied_generation IS NULL OR applied_generation = generation)");

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operations_deployment_id_generation_operati~1",
                table: "durable_create_operations",
                columns: new[] { "deployment_id", "generation", "operation_id", "current_attempt_id", "state" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operations_deployment_id_generation_operatio~",
                table: "durable_create_operations",
                columns: new[] { "deployment_id", "generation", "operation_id", "state" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations",
                sql: "reason_code IS NULL OR reason_code IN ('SecretUnavailable', 'Connectivity', 'Conflict', 'ServerRejected', 'UncertainAfterDispatch', 'InputInvalid', 'InputUnavailable', 'HandlerUnavailable')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_resource_id",
                table: "durable_create_operations",
                sql: "length(operation_id) > 0");

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operation_attempts_attempt_id_deployment_id_~",
                table: "durable_create_operation_attempts",
                columns: new[] { "attempt_id", "deployment_id", "generation", "operation_id", "attempt_number", "outcome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operation_attempts_deployment_id_generation_~",
                table: "durable_create_operation_attempts",
                columns: new[] { "deployment_id", "generation", "operation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operation_attempts_deployment_id_generation~1",
                table: "durable_create_operation_attempts",
                columns: new[] { "deployment_id", "generation", "operation_id", "attempt_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_durable_create_operation_attempts_durable_create_operations~",
                table: "durable_create_operation_attempts",
                columns: new[] { "deployment_id", "generation", "operation_id" },
                principalTable: "durable_create_operations",
                principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_postgresql_database_recovery_decisions_durable_create_oper~1",
                table: "postgresql_database_recovery_decisions",
                columns: new[] { "deployment_id", "generation", "operation_id" },
                principalTable: "durable_create_operations",
                principalColumns: new[] { "deployment_id", "generation", "operation_id" },
                onDelete: ReferentialAction.Restrict);
            // PostgreSQL composite foreign keys retain exact resolved-node/uncertain-attempt attribution.
            migrationBuilder.Sql("""
                ALTER TABLE postgresql_database_recovery_decisions
                  ADD CONSTRAINT FK_postgresql_recovery_committed_operation
                  FOREIGN KEY (deployment_id, generation, operation_id, attempt_id, operation_state)
                  REFERENCES durable_create_operations (deployment_id, generation, operation_id, current_attempt_id, state)
                  DEFERRABLE INITIALLY DEFERRED;
                ALTER TABLE postgresql_database_recovery_decisions
                  ADD CONSTRAINT FK_postgresql_recovery_uncertain_attempt
                  FOREIGN KEY (attempt_id, deployment_id, generation, operation_id, attempt_number, attempt_outcome)
                  REFERENCES durable_create_operation_attempts (attempt_id, deployment_id, generation, operation_id, attempt_number, outcome)
                  DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("This development model requires an explicitly initialized fresh catalog.");
    }
}
