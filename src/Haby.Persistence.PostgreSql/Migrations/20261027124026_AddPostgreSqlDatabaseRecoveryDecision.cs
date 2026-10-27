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
                    reason = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    technical_actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_postgresql_database_recovery_decisions", x => x.id);
                    table.CheckConstraint("CK_postgresql_recovery_acknowledged", "acknowledged = true");
                    table.CheckConstraint("CK_postgresql_recovery_actor", "length(btrim(technical_actor)) > 0 AND length(technical_actor) <= 256");
                    table.CheckConstraint("CK_postgresql_recovery_note", "length(btrim(note)) > 0 AND length(note) <= 512");
                    table.CheckConstraint("CK_postgresql_recovery_reason", "reason = 'OperatorAcceptedExistingDatabase'");
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
                name: "IX_postgresql_database_recovery_decisions_attempt_id",
                table: "postgresql_database_recovery_decisions",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_postgresql_database_recovery_decisions_deployment_id_genera~",
                table: "postgresql_database_recovery_decisions",
                columns: new[] { "deployment_id", "generation", "resource_id", "action" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION guard_postgresql_recovery_decision() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        RAISE EXCEPTION 'PostgreSQL recovery decision is immutable';
                    END IF;
                    IF NOT EXISTS (
                        SELECT 1 FROM durable_create_operations o
                        JOIN durable_create_operation_attempts a ON a.attempt_id = NEW.attempt_id
                        WHERE o.deployment_id = NEW.deployment_id AND o.generation = NEW.generation
                          AND o.resource_id = NEW.resource_id AND o.action = NEW.action
                          AND o.state = 'NeedsAttention' AND o.reason_code = 'UncertainAfterDispatch'
                          AND o.current_attempt_id = NEW.attempt_id AND o.attempt_number = a.attempt_number
                          AND o.dispatched_at IS NOT NULL AND o.finalized_at IS NOT NULL
                          AND a.deployment_id = NEW.deployment_id AND a.generation = NEW.generation
                          AND a.resource_id = NEW.resource_id AND a.action = NEW.action
                          AND a.dispatched_at IS NOT NULL AND a.finished_at IS NOT NULL
                          AND a.outcome = 'Uncertain') THEN
                        RAISE EXCEPTION 'recovery decision requires the current uncertain attempt';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER postgresql_recovery_decisions_before_mutation
                    BEFORE INSERT OR UPDATE OR DELETE ON postgresql_database_recovery_decisions
                    FOR EACH ROW EXECUTE FUNCTION guard_postgresql_recovery_decision();
                CREATE FUNCTION require_committed_postgresql_recovery_decision() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM durable_create_operations o
                        JOIN application_deployments d ON d.id = o.deployment_id
                        WHERE o.deployment_id = NEW.deployment_id AND o.generation = NEW.generation
                          AND o.resource_id = NEW.resource_id AND o.action = NEW.action
                          AND o.current_attempt_id = NEW.attempt_id AND o.state = 'Succeeded'
                          AND o.reason_code IS NULL AND d.desired_generation = NEW.generation
                          AND d.applied_generation = NEW.generation) THEN
                        RAISE EXCEPTION 'recovery decision must commit with succeeded applied generation';
                    END IF;
                    RETURN NULL;
                END
                $$;
                CREATE CONSTRAINT TRIGGER postgresql_recovery_decisions_committed
                    AFTER INSERT ON postgresql_database_recovery_decisions
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                    EXECUTE FUNCTION require_committed_postgresql_recovery_decision();
                """);

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
                           WHERE d.deployment_id = OLD.deployment_id AND d.generation = OLD.generation
                             AND d.resource_id = OLD.resource_id AND d.action = OLD.action
                             AND d.attempt_id = OLD.current_attempt_id AND d.acknowledged = true)
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
                """);
            migrationBuilder.Sql("""
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
                DROP TRIGGER postgresql_recovery_decisions_committed
                    ON postgresql_database_recovery_decisions;
                DROP FUNCTION guard_postgresql_recovery_decision();
                DROP FUNCTION require_committed_postgresql_recovery_decision();
                """);
            migrationBuilder.DropTable(
                name: "postgresql_database_recovery_decisions");
        }
    }
}
