# ADR-035: Provider-neutral persistence and resource name reservations

- Status: Accepted
- Date: 2026-11-03
- Scope: Provider persistence, including the application-login implementation in issue [#116](https://github.com/hamstix/haby/issues/116)
- Clarifies: ADR-024 and ADR-034

## Context

Provider-neutral configuration envelopes alone do not prevent later effects
from introducing Provider-specific catalog tables. Application-login work needs
a durable name reservation, and future adapters need the same invariant without
PostgreSQL role terminology in shared persistence.

The maintainer requires common storage with generic fields, not separate tables
or storage APIs for each Provider. Adapter ownership of behavior does not imply
ownership of a separate catalog schema.

## Decision

### Common storage, adapter-owned meaning

Haby-owned Provider state uses shared persistence entities and contracts with
generic identities and fields. Do not add Provider-specific tables, columns,
closed Provider enums or Provider-kind branches to common storage. In particular,
role-name reservation must not use a PostgreSQL role table or a `RoleName` column.
Adding another Provider implementation of the same capability must not require
new common catalog columns or a new per-Provider copy of that table.

Provider adapters own typed configuration/evidence DTOs, exact versioned codecs,
semantic validation, name derivation and external effects. Persist their specific
configuration and evidence through the existing generic versioned payload
boundaries, with explicit source-generated metadata. Common persistence stores
the envelope and opaque payload without interpreting adapter-specific fields.
Secret material remains in the dedicated SecretStore under ADR-006; this rule
does not authorize putting passwords or verifiers into generic payloads.

This is not a single universal table, EAV, arbitrary property bags or an
unvalidated JSON document model. Keep distinct shared concepts such as Provider
revisions, operation outputs, secrets and reservations, with typed generic fields,
foreign keys, transactions and declarative constraints appropriate to each.

### Minimal shared name reservations

Use one shared reservation model, for example `resource_name_reservations`, with:

- `ProviderInstanceId`;
- stable owner: `EnvironmentRecordId`, `ApplicationRecordId`, `ResourceLocalId`;
- opaque, bounded `Namespace` and `Value`.

ProviderInstance and owner Environment must agree. ProviderInstance is the
logical identity, not its current endpoint, alias, implementation or revision.
The stable owner contains no deployment generation or display name.

Enforce both uniqueness constraints in the database:

- `(ProviderInstanceId, Namespace, Value)` reserves a name for one owner;
- `(ProviderInstanceId, EnvironmentRecordId, ApplicationRecordId,
  ResourceLocalId, Namespace)` gives that owner one name per namespace.

The same owner/namespace/value is idempotent. A different owner requesting the
same name, or the same owner requesting a different name in that namespace,
conflicts without overwrite, release or random fallback. Concurrent requests
must converge or conflict through the same constraints and transaction rules.
Retain accepted reservations after operation failure, restart and restore.
Deletion, release, transfer and identity reuse remain deferred lifecycle work.

Namespace identifies the external naming scope/capability, not the version of
a generation algorithm. Its meaning and the value's syntax belong to the adapter;
the common layer only enforces the generic shape, scope and uniqueness contract.
For PostgreSQL roles, use one cluster-wide role namespace per ProviderInstance,
not separate namespaces per database. A future broker adapter can use its own
user namespace through the same storage API without PostgreSQL fields.

The reservation API is internal, asynchronous and Provider-neutral. It does not
create external objects or prove that an existing object belongs to Haby.
PostgreSQL's deterministic role-name computation, external collision checks and
no-adoption/no-password-reset rules remain adapter-owned under ADR-034.
Planning stays side-effect-free. Reservation acceptance belongs to the explicit
execution workflow, before its external effect; it never implies effect success.

Do not add leases, a policy registry, pluggable reservation backends, automated
cleanup or a universal resource lifecycle framework for this slice.

### Enforcement and current work

Apply this rule to new and in-flight Provider persistence, including issue #116
and PR #117. ADR-034's requirement to persist role name and uniqueness is fulfilled
by this shared reservation model; it does not authorize a Provider-specific table.

This documentation decision does not claim existing storage has been migrated
or audited. Existing specialized histories require separately scoped changes;
do not rewrite unrelated merged migrations. ADR-033 permits a fresh catalog for
an explicitly breaking implementation, never an automatic reset. ADR-026 still
requires C#/EF workflow and explicit approval for new server-side functions or
triggers; no mandatory database-role separation is introduced.

## Shared external systems across catalogs

[ADR-048](0048-environment-scoped-resource-naming.md) extends planning with an
Environment-to-Provider naming-policy selection before Workload implementation.
Reservations in this ADR remain local to one catalog: independent installations
on the same external server do not share ownership, locks or Provider identities.
Prefixes are neither proof of global name uniqueness nor authorization. External
collision handling and no-adoption checks remain adapter-owned.

## Required evidence

- Same-request idempotency, both uniqueness conflicts and concurrent acceptance
  are verified against real PostgreSQL with independent DbContexts.
- Provider and namespace isolation, Environment consistency, retained state on
  failure, restart and current-build restore preserve exact owner/name bindings.
- A typed non-PostgreSQL example exercises the same storage boundary without
  role/database fields or SQL semantics. It proves the shared seam, not a second
  production Provider implementation.
- Actual adapter call sites use the shared API. Naming, external checks and
  effect SQL remain outside common persistence.

## Consequences

New Providers reuse shared storage capabilities while retaining typed adapter
contracts. Generic fields preserve useful relational constraints without leaking
Provider-specific naming into catalog schemas. New capabilities may need new
shared entities, but a Provider-specific variant of an existing capability does
not justify a parallel table.
