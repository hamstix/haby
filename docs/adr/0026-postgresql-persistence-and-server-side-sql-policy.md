# ADR-026: Keep persistence behavior in C# and limit server-side SQL

- Status: Accepted
- Date: 2026-10-27
- Scope: Haby-owned PostgreSQL persistence, migrations, and provider adapters

## Context

Haby uses PostgreSQL as its source of truth and EF Core for persisted state.
The active pilot also needs PostgreSQL-specific capabilities, including row
locks, catalog inspection, and `CREATE DATABASE`. Several migrations currently
install PL/pgSQL trigger functions to enforce immutability or coordinate
cross-table state. These functions are application behavior deployed into each
customer database, with their own upgrade, rollback, backup/restore, and
diagnostic costs. Real PostgreSQL integration tests reduce regression risk but
do not eliminate those operational and maintenance costs.

This decision is about ownership and maintainability, not database
portability. Haby does not plan to replace PostgreSQL as its default store.
It does not weaken the immutable history, atomic transitions, concurrency
guards, or honest external-effect recovery required by earlier ADRs.

## Decision

### Default implementation boundary

Use EF Core and explicit C# application/persistence code for ordinary reads,
writes, state transitions, and orchestration. Keep domain decisions out of
database functions, procedures, and triggers. EF Core-generated SQL is not
considered raw SQL under this policy.

Prefer declarative database constraints for invariants that they express
directly: primary and foreign keys, unique constraints and indexes, `NOT NULL`,
and `CHECK` constraints. Use transactions and explicit concurrency conditions
for multi-record changes. A successful single-instance pilot is not a reason
to remove concurrency protection from persisted operations.

Do not add a stored procedure, PL/pgSQL function, or trigger implementing
workflow or cross-table business rules as the default solution. A simple
append-only guard is still server-side code and is subject to the same
exception review. Existing functions and triggers are not removed merely by
accepting this ADR; they must be inventoried and replaced or retained through
separate, behavior-preserving changes.

### Narrow raw-SQL exceptions

Use parameterized, localized SQL only where EF Core cannot express the needed
PostgreSQL operation or its required semantics clearly and reliably. Expected
examples are PostgreSQL DDL such as `CREATE DATABASE`, catalog queries,
explicit row-lock modes, and transaction-local PostgreSQL settings. Raw SQL
is not the default for ordinary entity CRUD or a shortcut around the EF model.

Each raw-SQL site must state the capability or invariant that requires it,
keep SQL close to its persistence/provider adapter, and have a real-PostgreSQL
test for its behavior, including relevant concurrency or failure cases.
Parameterize values; validate and quote identifiers that cannot be
parameterized. Do not interpolate secrets into SQL text or diagnostics.

An exception for new server-side functions or triggers requires an explicit
maintainer decision documenting why EF Core, C# transactions, concurrency
conditions, declarative constraints, and database permissions are insufficient;
the protected invariant; upgrade and rollback implications; and disposable
PostgreSQL tests. The exception must be as small as possible and must not
silently establish a general pattern for later features.

### Existing and in-flight work

Audit existing raw SQL and server-side functions in a separate follow-up.
Classify each site as required PostgreSQL capability, declarative integrity
constraint candidate, application workflow to move to C#, or justified
server-side exception. Preserve observable behavior and migration history;
never edit an already merged migration to remove a function or trigger.
Use additive migrations when changing deployed schema behavior, with
backup/restore and upgrade validation appropriate to the affected data.

The in-flight manual-recovery slice under ADR-025 must preserve its exact
attempt guards, immutable audit decision, atomic state transition and
`AppliedGeneration` advancement. Its implementation should first seek EF Core,
declarative constraints, transactions, and explicit concurrency checks rather
than extend PL/pgSQL workflow. If removing a database guard would weaken an
accepted invariant or permit a direct writer to falsify retained history,
the agent must stop and bring the narrow exception to the maintainer. The
pilot's single-instance boundary does not waive these safeguards.

## Consequences

- Most persistence behavior remains reviewable and refactorable with the C#
  model; database-specific operations remain explicit exceptions.
- Real PostgreSQL integration tests remain mandatory for SQL exceptions,
  migration behavior, concurrency, and rollback. EF Core InMemory tests do not
  establish those guarantees.
- A future audit may retain a few server-side integrity guards after explicit
  review. This ADR does not claim that C# checks alone protect against direct
  database mutation or privileged operators.
- Existing migration files and customer databases remain compatible while
  replacements are designed and tested incrementally.

## Rejected alternatives

### Ban every direct SQL statement

This would prevent or obscure required PostgreSQL operations and lock
semantics without improving Haby's maintainability.

### Allow server-side workflow because integration tests are comprehensive

Tests detect many defects but do not make PL/pgSQL easier to inspect, evolve,
deploy, or repair in customer installations. Domain workflow should have one
primary implementation in C#.

### Remove all existing triggers in one migration

That could silently weaken immutability or concurrency guarantees and would
mix unrelated storage changes into the active-pilot slice.
