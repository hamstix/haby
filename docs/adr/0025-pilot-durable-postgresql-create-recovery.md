# ADR-025: Durable PostgreSQL database creation and manual recovery for the active pilot

- Status: Accepted
- Date: 2026-10-27
- Scope: Single-instance active pilot, one `postgresql.database/v1alpha1` Resource

## Context

[ADR-031](0031-postgresql-execution-boundaries-and-reason-compatibility.md)
defines the accepted behavior-preserving relocation of database-specific
inputs/ports and the temporary compatibility treatment of durable reason codes.
It does not alter the execution or recovery policy below.

ADR-016 separates accepting a desired generation from executing external effects.
ADR-022 proves one PostgreSQL `CREATE DATABASE` through an in-memory checkpoint,
but its status does not survive a restart. The active pilot needs a durable
record of the exact operation and an honest recovery path when Haby loses the
response to `CREATE DATABASE` or crashes before recording success.

The absence check before dispatch is necessary but not proof of ownership.
Another actor could create the same database between that check and the
command. PostgreSQL also does not permit `CREATE DATABASE` inside a transaction
block, so creating the database and recording success in Haby cannot be one
atomic database transaction. A matching name observed later does not identify
which command created it. See the PostgreSQL
[`CREATE DATABASE` documentation](https://www.postgresql.org/docs/current/sql-createdatabase.html).

This ADR is a narrow pilot policy, not the general M3 operation framework
reserved for ADR-005. It does not relax the single-instance, network-isolated
pilot boundary or authorize database deletion, principal creation, credential
delivery, or a stable public recovery API.

## Decision

### Durable operation identity and exact inputs

One operation is uniquely keyed by the accepted
`ApplicationDeploymentGeneration`, the Resource ID, and the `Create` action.
It references the immutable generation and its exact ProviderInstance,
configuration revision, and ImplementationId. It never follows the current
Provider alias at execution time and never copies a Provider payload,
SecretVersion material, connection string, or password into its journal.

An operation record must be committed before any external effect. The desired
generation commit may create the initial operation in the same transaction;
alternatively a restart-safe scanner may idempotently create a missing
`Pending` operation from an accepted but unapplied generation. In either case,
no `CREATE DATABASE` is dispatched until that durable record exists. A unique
database constraint prevents a second operation for the same generation,
Resource, and action. Each execution attempt is separately attributable and
retained; retry never erases a prior attempt or invents a new desired generation.

### Dispatch barrier and states

The pilot operation has these durable states:

| State | Meaning after a restart |
| --- | --- |
| `Pending` | No attempt has crossed the dispatch barrier; execution may start. |
| `Preparing` | Only local validation, pinned-input resolution, connectivity, and the database-absence check may have run; the attempt may be safely resumed or restarted. |
| `Dispatching` | The dispatch marker was durably committed before the driver call. The command may or may not have reached PostgreSQL; recovery must move the operation to `NeedsAttention` without sending SQL. |
| `Succeeded` | Haby recorded confirmed success and the corresponding applied-generation advancement; no new `CREATE` is sent. |
| `Failed` | A definitive, value-free failure or conflict was recorded; no automatic post-dispatch retry occurs. |
| `NeedsAttention` | The external outcome is uncertain and only an explicit operator recovery decision may resolve it. |

The executor first validates the exact pinned configuration, secret and
implementation and checks that the target database is absent. An existing
database before this attempt is a conflict, not a successful retry. Immediately
before invoking `CREATE DATABASE`, it commits a `Dispatching` marker with a
unique attempt identity. The marker is deliberately conservative: a crash
between committing it and sending SQL still becomes `NeedsAttention`.

The executor may record `Succeeded` only after PostgreSQL confirms creation and
an independent read confirms that the database exists. It records the terminal
operation result and advances `AppliedGeneration` for the still-current pilot
generation atomically in Haby's store. If Haby loses the response or cannot
persist success, the durable `Dispatching` marker makes the outcome uncertain.
Finding a database on restart does not by itself turn that outcome into success.

A definitive PostgreSQL rejection, including `duplicate_database`, is a
failure or conflict rather than proof that this attempt created the database.
Connection loss, timeout, cancellation, process termination, or an unclassified
error after the dispatch marker is committed cannot prove non-execution and
therefore yields `NeedsAttention`. Cancellation before that marker may stop the
attempt without implying an external effect. A cancelled request must not
erase the durable marker; recovery must remain possible after process death.

The single pilot instance serializes execution for one operation key and uses
durable compare-and-transition guards so concurrent requests cannot dispatch
two attempts. Distributed leases and multi-replica scheduling remain deferred.

### Explicit manual assignment after an uncertain outcome

An operator may resolve `NeedsAttention` by explicitly consenting to assign
an existing database to the exact operation. This is a distinct recovery
action, never a consequence of ordinary retry, startup reconciliation, a
matching name, or merely viewing the operation. The operator must be shown the
Environment, Application, generation, Resource, database name, exact Provider
identity, attempt, and uncertainty before confirming. The request identifies
that exact operation and carries an affirmative acknowledgment of assignment,
a bounded explanation, and the trusted technical actor. An absent or implicit
acknowledgment is rejected. The alpha CLI/UI must not preselect consent.

Haby performs a fresh read-only existence check against the pinned Provider
target and rejects assignment if the database is absent or the target cannot
be verified. Existence is a necessary condition, **not** proof that Haby's
command created the database. The operator assumes responsibility for that
provenance judgment and explicitly accepts ownership on Haby's behalf. Haby
persists the immutable recovery decision, actor, time, explanation, and exact
operation identity. The decision and the transition to `Succeeded` and
`AppliedGeneration` are atomic. No `CREATE DATABASE` is sent during assignment.

The pilot has no application-level operator authentication. Its network
isolation and operator-controlled console are therefore part of the trust
boundary. A recorded technical actor and affirmative acknowledgment prove
what the allowed caller submitted, not the legal identity of an individual.
The runbook must state this limitation; stronger human attribution requires a
later authentication and authorization capability.

If provenance is doubtful, the operator leaves the operation in
`NeedsAttention`. Haby never automatically adopts the database, even when a
pre-dispatch absence check was recorded. A database that existed before the
attempt remains a conflict and is not eligible for this uncertain-outcome
assignment path.

If the database is absent after an uncertain attempt, absence alone is not
permission to retry: the prior server command may still be running. A new
attempt requires a separate explicit operator decision after checking that
the old command can no longer complete, a fresh absence check on the pinned
target, and an audit explanation. No blind or automatic retry after the
dispatch barrier is allowed. If these conditions cannot be established, the
operation stays in `NeedsAttention`.

### Observability, retention, and test boundary

Ordinary operation reads expose exact non-secret identities, state, bounded
reason codes, attempt times, and the manual-decision audit, but never secret
values or raw driver errors. A corrupted or unavailable pinned revision fails
closed; the executor cannot substitute the current alias target. Accepted
generation history and exact Provider/SecretVersion references needed for
recovery remain retained under ADR-024.

Before enabling live pilot execution, tests against disposable PostgreSQL
must demonstrate: restart before and after the dispatch marker; cancellation
and connection loss after dispatch; a confirmed success with no second
`CREATE`; conflict on a preexisting database; a crash after external success
but before Haby records it; manual assignment requiring affirmative consent
and a reason; rejection of assignment when the database is absent; and an
audited, explicitly authorized retry only when the previous attempt cannot
still create the database. Fault injection should make the relevant crash
windows deterministic. A backup/restore rehearsal must preserve operation,
attempt, and recovery-decision history without claiming an uncertain result
was successful.

## Consequences

- The pilot never silently converts an uncertain external outcome into success.
- Manual assignment is supported, but the operator explicitly accepts the
  provenance risk and leaves an attributable audit record.
- Conservative `Dispatching` recovery can require human attention even when
  SQL was never sent. This is preferable to an unsafe second `CREATE`.
- A successful external effect may temporarily remain unapplied in Haby until
  an operator resolves the lost-confirmation window.
- Future ownership markers or an exclusive Provider-target policy may support
  stronger automated recovery, but neither is assumed here.

## Rejected alternatives

### Automatically assign a matching database after an uncertain result

The earlier absence check and later name match do not prove that Haby's
attempt created the database. They are separated by an external race and
cannot be made atomic with Haby's journal.

### Retry every interrupted `CREATE DATABASE`

A connection error or cancellation does not prove the command was not
executed. A second command could conflict with or obscure the first result.

### Treat a request cancellation as rollback of the external command

Cancellation after the dispatch barrier cannot establish that PostgreSQL
rolled back or never committed the command.

## Follow-up work

- Implement the pilot operation journal, dispatch barrier, restart recovery,
  explicit manual recovery actions, and real-PostgreSQL fault-injection tests
  in reviewable slices after durable desired-state commit.
- Define broader multi-Resource reconciliation, leases, compensation, and
  automatic ownership evidence in ADR-005 when those capabilities are needed.
