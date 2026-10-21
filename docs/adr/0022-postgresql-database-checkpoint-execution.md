# ADR-022: PostgreSQL database checkpoint execution

- Status: Accepted
- Date: 2026-10-21

## Context

The PostgreSQL walking skeleton in [ROADMAP.md](../../ROADMAP.md) must prove that
an imported Application manifest can lead to an observable external Resource.
ADR-016 already separates immutable desired-state acceptance from external
execution. The initial in-memory ChangeSet/Plan/commit path implements that
boundary, but does not call a Provider or advance `AppliedGeneration`.

This checkpoint is deliberately narrower than a production Provider SDK or the
durable operation model deferred to ADR-005. It must nevertheless avoid false
success, accidental adoption of an existing database, unsafe SQL identifiers
and tests that only verify mocked driver calls.

## Decision

### One exact Resource contract

The checkpoint registers one canonical Resource type definition and one
build-time-composed implementation for `postgresql.database/v1alpha1`. Contract
identity is independent of the implementing assembly and Provider instance, as
required by ADR-018. Planning verifies that the exact selected implementation
supports this contract; an alias resolving to a different capability is not a
valid execution Plan.

The v1alpha1 `properties` schema is a closed Object with one required member:

```json
{
  "name": "application_data"
}
```

`name` is the explicit PostgreSQL database identifier. The checkpoint has no
implicit name generator, Liquid evaluation or Environment-specific name
substitution. The name is 1–63 ASCII bytes and matches
`^[a-z][a-z0-9_]*$`; an empty Object, unknown member or invalid name is
rejected during manifest contract validation, before planning or SQL execution.
The exact name is part of the accepted immutable manifest revision. A future
contract version or planning input may add generated or Environment-specific
names without changing this contract implicitly.

The implementation validates the identifier again at its trust boundary and
quotes it as a PostgreSQL identifier. SQL value parameters cannot substitute
for an identifier. It never inserts unchecked manifest text into SQL and never
reuses the prototype PostgreSQL strategy's interpolated statements.

### Exact Provider configuration stays outside the manifest

The administrative connection and credentials belong to operator-owned
Provider configuration, not to Application `properties`. The Plan and accepted
generation snapshot pin the Provider instance, configuration revision and
implementation identity selected during planning. They do not contain the
connection string or credential values. Execution loads the pinned revision;
it must not silently follow a changed alias or a newer configuration revision.
An unavailable or incompatible pinned configuration fails before provisioning.

This checkpoint may hold Provider configuration in a test-scoped in-memory
catalog. It does not establish a persisted Provider aggregate or secret store.
Sensitive configuration must not appear in operation results, diagnostics,
logs, snapshots or fixtures committed to the repository.

### External execution follows desired-state commit

Import and Plan remain side-effect free. Applying a Plan commits an immutable
desired generation as specified by ADR-016; it does not execute SQL.
A separate checkpoint-only executor consumes the committed exact generation and
its pinned Provider inputs. It supports one `postgresql.database` Resource and
does not claim success for any other undeployed Resource or Workload in that
generation. Unsupported shapes fail closed.

The executor exposes an observable, in-memory status for the exact generation
and Resource: `Pending`, `Running`, `Succeeded`, `Failed` or `NeedsAttention`.
These are checkpoint-local states, not a stable public operation API. At most
one execution for the same key may run at a time. A confirmed successful
execution can be retried without another `CREATE DATABASE`. A definitive
failure before SQL dispatch may be retried; an ambiguous result may not be
blindly retried. Cancellation is propagated, but cancellation after SQL was
sent does not prove that the database was not created.

`AppliedGeneration` advances only after every external effect required by the
accepted generation is confirmed successful. For this checkpoint, that can be
demonstrated only for a generation containing the single supported database
Resource and no other external effects. A committed desired generation may
therefore remain unapplied or have a failed execution. The in-memory status
and `AppliedGeneration` do not survive a restart; durable recovery belongs to
ADR-005 and later persistence work.

### Existing databases and uncertain outcomes

The executor never adopts a database that existed before its own confirmed
successful operation. Encountering such a database is a conflict, not
idempotent success. In particular, matching the requested name does not prove
ownership.

If connection loss, cancellation or another interruption occurs after a
creation command may have reached PostgreSQL but before success is confirmed
and recorded, the result is `NeedsAttention`. Haby must not report success,
automatically adopt the database or blindly retry creation. A later manual or
durable recovery workflow may resolve that state. In this checkpoint, a process
restart can similarly lose the in-memory confirmation and cause a conflict;
restart-safe recovery is explicitly out of scope.

Only database creation is supported. The checkpoint does not create roles,
grant access, generate passwords, emit connection strings, publish
Configuration documents or drop a database. Lifecycle deletion policies do not
authorize deprovisioning here.

### Test boundary

Fast Application-level tests fake Haby's own execution boundary to verify
commit-to-execution orchestration, status transitions, retries and generation
accounting. They do not mock `Npgsql` types as evidence that PostgreSQL SQL
works.

At least one integration test uses Npgsql against an ephemeral PostgreSQL
instance to prove database creation and independent existence verification,
confirmed retry, existing-database conflict, safe name handling and visible
failure. `Testcontainers.PostgreSql` is the preferred test-only harness if its
current version, license and transitive dependencies pass repository review.
It requires a Docker-API-compatible runtime. The integration suite is clearly
identified; ordinary unit tests never connect to a developer's PostgreSQL.
CI must run the real-PostgreSQL test and must not silently skip it when the
container runtime is unavailable. A maintainer's existing PostgreSQL instance
may be used only for an explicitly opted-in, isolated manual smoke test.

## Consequences

- The checkpoint can falsify the architecture using a real PostgreSQL side
  effect without presenting the in-memory operation path as production-ready.
- Import, Plan, desired-state commit and external execution remain distinct.
- Retrying a confirmed execution is safe, while an uncertain outcome remains
  visible rather than becoming a false success.
- A test-only container dependency and runtime are required for the automated
  provider integration check; package acceptance is verified before addition.
- Environment-specific names, database ownership markers, durable execution,
  recovery and the general Provider SDK remain later decisions.

## Rejected alternatives

### Mock Npgsql as the only Provider test

Mocked driver calls cannot prove PostgreSQL syntax, identifier safety,
privileges, existence checks or the observable database side effect.

### Provision during import or desired-state commit

This would break ADR-016's atomic desired-state boundary and make manifest
import Environment-dependent.

### Treat every existing database as a successful retry

A name match does not establish that Haby created or owns the database. It
could silently adopt unrelated operator state.

### Use a maintainer's PostgreSQL instance as the default test dependency

Tests would become environment-dependent and risk modifying persistent data.

## Follow-up work

- Specify and implement the checkpoint-only executor and integration test in
  the next dependent coding slice.
- Add the thin gRPC test-host adapter and end-to-end test after the in-process
  PostgreSQL path is verified.
- Design durable operation status, ownership verification and recovery in
  ADR-005 before a production provisioning release.
