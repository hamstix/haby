using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableCreateDispatchBarrier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_state",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments");

            migrationBuilder.AddColumn<long>(
                name: "attempt_number",
                table: "durable_create_operations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "current_attempt_id",
                table: "durable_create_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dispatched_at",
                table: "durable_create_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finalized_at",
                table: "durable_create_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason_code",
                table: "durable_create_operations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "durable_create_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "durable_create_operation_attempts",
                columns: table => new
                {
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_number = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_create_operation_attempts", x => x.attempt_id);
                    table.CheckConstraint("CK_durable_create_operation_attempts_action", "action = 'Create'");
                    table.CheckConstraint("CK_durable_create_operation_attempts_close", "(outcome IS NULL) = (finished_at IS NULL)");
                    table.CheckConstraint("CK_durable_create_operation_attempts_number", "attempt_number > 0");
                    table.CheckConstraint("CK_durable_create_operation_attempts_outcome", "outcome IS NULL OR outcome IN ('Succeeded', 'Conflict', 'ServerRejected', 'Uncertain', 'CanceledBeforeDispatch', 'AbandonedBeforeDispatch', 'FailedBeforeDispatch')");
                    table.ForeignKey(
                        name: "FK_durable_create_operation_attempts_durable_create_operations~",
                        columns: x => new { x.deployment_id, x.generation, x.resource_id, x.action },
                        principalTable: "durable_create_operations",
                        principalColumns: new[] { "deployment_id", "generation", "resource_id", "action" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_attempt",
                table: "durable_create_operations",
                sql: "attempt_number >= 0 AND (attempt_number = 0) = (current_attempt_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_dispatch",
                table: "durable_create_operations",
                sql: "(state = 'Dispatching') = (dispatched_at IS NOT NULL AND finalized_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_final",
                table: "durable_create_operations",
                sql: "(state IN ('Succeeded', 'Failed', 'NeedsAttention')) = (finalized_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_needs_attention",
                table: "durable_create_operations",
                sql: "reason_code IS DISTINCT FROM 'UncertainAfterDispatch' OR state = 'NeedsAttention'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations",
                sql: "reason_code IS NULL OR reason_code IN ('ProviderInputUnavailable', 'SecretUnavailable', 'UnsupportedShape', 'DatabaseNameInvalid', 'Connectivity', 'Conflict', 'ServerRejected', 'UncertainAfterDispatch')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_state",
                table: "durable_create_operations",
                sql: "state IN ('Pending', 'Preparing', 'Dispatching', 'Succeeded', 'Failed', 'NeedsAttention')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments",
                sql: "desired_generation = 1 AND (applied_generation IS NULL OR applied_generation = 1)");

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operation_attempts_deployment_id_generation_~",
                table: "durable_create_operation_attempts",
                columns: new[] { "deployment_id", "generation", "resource_id", "action" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operation_attempts_deployment_id_generation~1",
                table: "durable_create_operation_attempts",
                columns: new[] { "deployment_id", "generation", "resource_id", "action", "attempt_number" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION guard_durable_create_operation_mutation() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.deployment_id IS DISTINCT FROM NEW.deployment_id
                       OR OLD.generation IS DISTINCT FROM NEW.generation
                       OR OLD.resource_id IS DISTINCT FROM NEW.resource_id
                       OR OLD.action IS DISTINCT FROM NEW.action THEN
                        RAISE EXCEPTION 'durable create operation identity is immutable';
                    END IF;
                    IF OLD.state IN ('Succeeded', 'Failed', 'NeedsAttention') THEN
                        RAISE EXCEPTION 'terminal durable create operation is immutable';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER durable_create_operations_before_update BEFORE UPDATE ON durable_create_operations
                    FOR EACH ROW EXECUTE FUNCTION guard_durable_create_operation_mutation();
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION guard_durable_create_operation_attempt_mutation() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.attempt_id IS DISTINCT FROM NEW.attempt_id
                       OR OLD.deployment_id IS DISTINCT FROM NEW.deployment_id
                       OR OLD.generation IS DISTINCT FROM NEW.generation
                       OR OLD.resource_id IS DISTINCT FROM NEW.resource_id
                       OR OLD.action IS DISTINCT FROM NEW.action
                       OR OLD.attempt_number IS DISTINCT FROM NEW.attempt_number THEN
                        RAISE EXCEPTION 'durable create operation attempt identity is immutable';
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'durable create operation attempt is append-only';
                    END IF;
                    IF OLD.finished_at IS NOT NULL THEN
                        RAISE EXCEPTION 'closed durable create operation attempt is immutable';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER durable_create_operation_attempts_before_mutation
                    BEFORE UPDATE OR DELETE ON durable_create_operation_attempts
                    FOR EACH ROW EXECUTE FUNCTION guard_durable_create_operation_attempt_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER durable_create_operation_attempts_before_mutation ON durable_create_operation_attempts;
                DROP FUNCTION guard_durable_create_operation_attempt_mutation();
                DROP TRIGGER durable_create_operations_before_update ON durable_create_operations;
                DROP FUNCTION guard_durable_create_operation_mutation();
                """);
            migrationBuilder.DropTable(
                name: "durable_create_operation_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_attempt",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_dispatch",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_final",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_needs_attention",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_reason",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operations_state",
                table: "durable_create_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments");

            migrationBuilder.DropColumn(
                name: "attempt_number",
                table: "durable_create_operations");

            migrationBuilder.DropColumn(
                name: "current_attempt_id",
                table: "durable_create_operations");

            migrationBuilder.DropColumn(
                name: "dispatched_at",
                table: "durable_create_operations");

            migrationBuilder.DropColumn(
                name: "finalized_at",
                table: "durable_create_operations");

            migrationBuilder.DropColumn(
                name: "reason_code",
                table: "durable_create_operations");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "durable_create_operations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operations_state",
                table: "durable_create_operations",
                sql: "state = 'Pending'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_application_deployments_desired_generation",
                table: "application_deployments",
                sql: "desired_generation = 1 AND applied_generation IS NULL");
        }
    }
}
