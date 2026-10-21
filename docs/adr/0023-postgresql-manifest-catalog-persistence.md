# ADR-023: PostgreSQL persistence for the immutable manifest catalog

- Status: Accepted
- Date: 2026-10-21

## Context

The architecture checkpoint proves manifest import and PostgreSQL provisioning
with in-memory Haby state. The shadow pilot needs accepted Applications and
ManifestRevisions to survive a process restart without changing the accepted
import semantics in ADR-015 and ADR-021. The prototype has no deployed storage
format that must be preserved.

## Decision

### EF Core is the catalog persistence boundary

Use Entity Framework Core with `Npgsql.EntityFrameworkCore.PostgreSQL` for the
durable Haby catalog. Introduce a dedicated `DbContext` and persistence entities;
do not make domain models or manifest DTOs EF entities. The Application layer
continues to depend on persistence-neutral ports. The first persistence slice
does not introduce a parallel handwritten-SQL repository.

All operations against the durable store, including reads, writes, transactions
and migrations, are asynchronous and accept cancellation where applicable.
Do not block on asynchronous database work to implement the current synchronous
revision reader; evolve that port before using PostgreSQL-backed planning.

### Persist a versioned normalized snapshot

Persist each accepted immutable revision's normalized declaration using an
explicitly versioned internal snapshot format. Retain its input API version,
normalized-model version, historical warnings and first-import audit metadata.
Rehydration does not parse or revalidate the original manifest and does not
require the original input API adapter. The snapshot codec must preserve the
typed graph's equality semantics, including exact JSON numbers, ordered arrays,
text templates, absent-versus-null distinctions and materialized core defaults.
Exact numbers must not round-trip through `double` or `decimal`.

The internal snapshot is not the public manifest format or an alternative
authority for declaration equality. A change to its encoding requires an
explicit format version and migration or compatibility reader. Corrupt or
unsupported stored snapshots fail visibly; they are not silently replaced from
an incoming manifest. The first slice need not persist original source bytes or
introduce a fingerprint optimization.

### Preserve atomic compare-and-create

The first accepted revision and its Application record commit atomically.
Database constraints enforce unique Application IDs, unique
`ApplicationId + ApplicationVersion` pairs and unique
`ApplicationRecordId + LocalRevisionNumber` pairs. Local revision numbers start
at 1 and increase monotonically; they need not be gapless. Concurrent equivalent
or conflicting imports resolve to the same typed outcomes as the in-memory
store. A losing import never mutates the winning declaration, warnings, actor or
timestamp. A rejected import never leaves an empty Application.

The persistence adapter owns transaction and conflict handling. An uncoordinated
read-then-insert sequence and broad exception-to-conflict conversion are not
acceptable. Cancellation before commit may leave no revision; after the store
confirms commit, the use case must not report the committed import as canceled.

### Apply reviewed migrations on single-instance startup

The early pilot runs exactly one Haby instance. On startup, before accepting
requests, it applies pending reviewed EF Core migrations asynchronously and
fails closed if migration fails. Do not use `EnsureCreated` as the deployed
schema-management mechanism. The deployment procedure includes backup and
restore instructions and verifies that the configured database principal has
the privileges required for this startup migration model. This is an early-pilot
policy, not a promise that future multi-instance or production deployments
will migrate from every application replica.

### Test at the appropriate boundary

`Microsoft.EntityFrameworkCore.InMemory` may support fast, isolated tests of
simple EF-backed behavior. It does not prove PostgreSQL transactions, database
uniqueness, concurrency, migrations or provider-specific query behavior. Those
invariants require integration tests against disposable real PostgreSQL, using
separate `DbContext` instances for arrange, act and assert. The existing
in-memory application store remains useful for tests that do not exercise EF.

## Consequences

- The first durable slice can reuse the accepted import use case and its typed
  outcomes while replacing only its persistence adapter.
- Snapshot compatibility and PostgreSQL race behavior become explicit test
  obligations before a deployable shadow pilot.
- EF Core and the Npgsql provider are runtime dependencies of the persistence
  project; the InMemory provider is test-only and is not evidence that the
  PostgreSQL adapter is correct.
- Startup migration is operationally simple for the single-instance pilot but
  requires schema privileges and a tested backup and failure procedure.

## Deferred work

- Persisted ChangeSets, Plans, generations and execution status for the active
  pilot are separate slices.
- The internal alpha operator API and its network boundary are defined by the
  pilot deployment, not by the manifest persistence adapter.
- A later deployment profile may move migrations to a separate job and reduce
  the application database principal's DDL privileges.
