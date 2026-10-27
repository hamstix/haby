using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddPostgreSqlDatabaseRecoveryDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "postgresql_database_recovery_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<long>(type: "bigint", nullable: false),
                    attempt_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Uncertain"),
                    reason = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    technical_actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Succeeded"),
                    applied_generation = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_postgresql_database_recovery_decisions", x => x.id);
                    table.CheckConstraint("CK_postgresql_recovery_acknowledged", "acknowledged = true");
                    table.CheckConstraint("CK_postgresql_recovery_actor", "length(btrim(technical_actor)) > 0 AND length(technical_actor) <= 256");
                    table.CheckConstraint("CK_postgresql_recovery_committed_state", "operation_state = 'Succeeded' AND applied_generation = generation");
                    table.CheckConstraint("CK_postgresql_recovery_note", "length(btrim(note)) > 0 AND length(note) <= 512");
                    table.CheckConstraint("CK_postgresql_recovery_reason", "reason = 'OperatorAcceptedExistingDatabase'");
                    table.CheckConstraint("CK_postgresql_recovery_uncertain_attempt", "attempt_outcome = 'Uncertain'");
                    table.ForeignKey(
                        name: "FK_postgresql_database_recovery_decisions_durable_create_opera~",
                        column: x => x.attempt_id,
                        principalTable: "durable_create_operation_attempts",
                        principalColumn: "attempt_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_postgresql_database_recovery_decisions_durable_create_oper~1",
                        columns: x => new { x.deployment_id, x.generation, x.resource_id, x.action },
                        principalTable: "durable_create_operations",
                        principalColumns: new[] { "deployment_id", "generation", "resource_id", "action" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operations_deployment_id_generation_resourc~1",
                table: "durable_create_operations",
                columns: new[] { "deployment_id", "generation", "resource_id", "action", "current_attempt_id", "state" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operations_deployment_id_generation_resource~",
                table: "durable_create_operations",
                columns: new[] { "deployment_id", "generation", "resource_id", "action", "state" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_create_operation_attempts_attempt_id_deployment_id_~",
                table: "durable_create_operation_attempts",
                columns: new[] { "attempt_id", "deployment_id", "generation", "resource_id", "action", "attempt_number", "outcome" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_durable_create_operation_attempts_uncertain_dispatch",
                table: "durable_create_operation_attempts",
                sql: "outcome IS DISTINCT FROM 'Uncertain' OR dispatched_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_application_deployments_id_applied_generation",
                table: "application_deployments",
                columns: new[] { "id", "applied_generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_postgresql_database_recovery_decisions_attempt_id",
                table: "postgresql_database_recovery_decisions",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_postgresql_database_recovery_decisions_deployment_id_genera~",
                table: "postgresql_database_recovery_decisions",
                columns: new[] { "deployment_id", "generation", "resource_id", "action" },
                unique: true);
            // EF Core does not expose PostgreSQL's DEFERRABLE foreign key mode. The decision is
            // inserted before the guarded operation/deployment updates in one transaction; these
            // declarative constraints require the four rows to agree at commit, including for a
            // direct writer. They replace the prior cross-table insertion and deferred triggers.
            migrationBuilder.Sql("""
                ALTER TABLE postgresql_database_recovery_decisions
                    ADD CONSTRAINT FK_postgresql_recovery_committed_operation
                    FOREIGN KEY (deployment_id, generation, resource_id, action, attempt_id, operation_state)
                    REFERENCES durable_create_operations
                        (deployment_id, generation, resource_id, action, current_attempt_id, state)
                    DEFERRABLE INITIALLY DEFERRED;
                ALTER TABLE postgresql_database_recovery_decisions
                    ADD CONSTRAINT FK_postgresql_recovery_uncertain_attempt
                    FOREIGN KEY (attempt_id, deployment_id, generation, resource_id, action,
                                 attempt_number, attempt_outcome)
                    REFERENCES durable_create_operation_attempts
                        (attempt_id, deployment_id, generation, resource_id, action,
                         attempt_number, outcome)
                    DEFERRABLE INITIALLY DEFERRED;
                ALTER TABLE postgresql_database_recovery_decisions
                    ADD CONSTRAINT FK_postgresql_recovery_applied_generation
                    FOREIGN KEY (deployment_id, applied_generation)
                    REFERENCES application_deployments (id, applied_generation)
                    DEFERRABLE INITIALLY DEFERRED;
                """);

            // The current migration/runtime owner can update tables directly. C# and CHECK/FK
            // constraints cannot compare a retained decision's old and new text or block DELETE.
            migrationBuilder.Sql("""
                CREATE FUNCTION guard_postgresql_recovery_decision_mutation() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'PostgreSQL recovery decision is immutable';
                END
                $$;
                CREATE TRIGGER postgresql_recovery_decisions_before_mutation
                    BEFORE UPDATE OR DELETE ON postgresql_database_recovery_decisions
                    FOR EACH ROW EXECUTE FUNCTION guard_postgresql_recovery_decision_mutation();
                """);

            // The merged guard rejects all terminal updates. Its sole exception retains the
            // same operation identity, attempt and timestamps and requires an inserted decision
            // for the matching closed, post-dispatch Uncertain attempt. No other terminal rewrite
            // or delete is permitted. The deferred FKs above require success plus application.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION guard_durable_create_operation_mutation() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD.state IN ('Succeeded', 'Failed', 'NeedsAttention') THEN
                            RAISE EXCEPTION 'terminal durable create operation is immutable';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF OLD.deployment_id IS DISTINCT FROM NEW.deployment_id
                       OR OLD.generation IS DISTINCT FROM NEW.generation
                       OR OLD.resource_id IS DISTINCT FROM NEW.resource_id
                       OR OLD.action IS DISTINCT FROM NEW.action THEN
                        RAISE EXCEPTION 'durable create operation identity is immutable';
                    END IF;
                    IF OLD.state = 'NeedsAttention' AND NEW.state = 'Succeeded'
                       AND OLD.reason_code = 'UncertainAfterDispatch'
                       AND NEW.reason_code IS NULL
                       AND OLD.current_attempt_id IS NOT NULL
                       AND NEW.current_attempt_id IS NOT DISTINCT FROM OLD.current_attempt_id
                       AND NEW.attempt_number IS NOT DISTINCT FROM OLD.attempt_number
                       AND NEW.created_at IS NOT DISTINCT FROM OLD.created_at
                       AND NEW.dispatched_at IS NOT DISTINCT FROM OLD.dispatched_at
                       AND NEW.finalized_at IS NOT DISTINCT FROM OLD.finalized_at
                       AND NEW.updated_at >= OLD.updated_at
                       AND EXISTS (
                           SELECT 1 FROM postgresql_database_recovery_decisions d
                           JOIN durable_create_operation_attempts a ON a.attempt_id = d.attempt_id
                           WHERE d.deployment_id = OLD.deployment_id AND d.generation = OLD.generation
                             AND d.resource_id = OLD.resource_id AND d.action = OLD.action
                             AND d.attempt_id = OLD.current_attempt_id AND d.acknowledged = true
                             AND a.deployment_id = OLD.deployment_id AND a.generation = OLD.generation
                             AND a.resource_id = OLD.resource_id AND a.action = OLD.action
                             AND a.attempt_number = OLD.attempt_number
                             AND a.dispatched_at IS NOT NULL AND a.finished_at IS NOT NULL
                             AND a.outcome = 'Uncertain')
                    THEN
                        RETURN NEW;
                    END IF;
                    IF OLD.state IN ('Succeeded', 'Failed', 'NeedsAttention') THEN
                        RAISE EXCEPTION 'terminal durable create operation is immutable';
                    END IF;
                    RETURN NEW;
                END
                $$;
                DROP TRIGGER durable_create_operations_before_update ON durable_create_operations;
                CREATE TRIGGER durable_create_operations_before_mutation
                    BEFORE UPDATE OR DELETE ON durable_create_operations
                    FOR EACH ROW EXECUTE FUNCTION guard_durable_create_operation_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM postgresql_database_recovery_decisions) THEN
                        RAISE EXCEPTION 'cannot discard retained PostgreSQL recovery decisions';
                    END IF;
                END $$;
                DROP TRIGGER durable_create_operations_before_mutation ON durable_create_operations;
                CREATE OR REPLACE FUNCTION guard_durable_create_operation_mutation() RETURNS trigger
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
                CREATE TRIGGER durable_create_operations_before_update
                    BEFORE UPDATE ON durable_create_operations
                    FOR EACH ROW EXECUTE FUNCTION guard_durable_create_operation_mutation();
                DROP TRIGGER postgresql_recovery_decisions_before_mutation
                    ON postgresql_database_recovery_decisions;
                DROP FUNCTION guard_postgresql_recovery_decision_mutation();
                """);
            migrationBuilder.DropTable(
                name: "postgresql_database_recovery_decisions");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operations_deployment_id_generation_resourc~1",
                table: "durable_create_operations");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operations_deployment_id_generation_resource~",
                table: "durable_create_operations");

            migrationBuilder.DropIndex(
                name: "IX_durable_create_operation_attempts_attempt_id_deployment_id_~",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_durable_create_operation_attempts_uncertain_dispatch",
                table: "durable_create_operation_attempts");

            migrationBuilder.DropIndex(
                name: "IX_application_deployments_id_applied_generation",
                table: "application_deployments");
        }
    }
}
