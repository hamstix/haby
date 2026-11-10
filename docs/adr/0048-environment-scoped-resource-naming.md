# ADR-048: Environment-scoped Resource naming on shared external systems

- Status: Accepted
- Date: 2026-11-10
- Scope: Adoption prerequisite before P3 Workload implementation
- Implementation: [Issue #175](https://github.com/hamstix/haby/issues/175), draft [PR #176](https://github.com/hamstix/haby/pull/176); [current contract](../architecture/resource-naming-v1alpha1.md) and [operator path/evidence boundaries](../development/175-environment-resource-naming.md)
- Extends: ADR-018/022/024/026/030/033/034/035/045

## Context

Independent Haby installations can manage different contours on the same external
system. For example, one PostgreSQL server may contain staging_orders and
staging_shard_orders, with equivalent requirements for future broker users.
A portable immutable Application revision must not need different literal names
for each Environment. Separate Haby catalogs cannot see each other's reservations.

The current PostgreSQL contract treats properties.name as the literal database
name. Generated application roles already derive from stable Resource owner IDs.
This ADR accepts an explicit naming extension; it does not claim implementation
or reopen deferred Liquid/parameter execution. Implement this narrow adoption
prerequisite before starting Workload execution.

## Decision

### Logical declarations and resolved external names

Keep the Application Resource declaration Environment-independent:

```json
{
  "type": "postgresql.database/v1alpha1",
  "providerRef": "postgresql.primary",
  "properties": { "name": "orders" }
}
```

The selected naming mode determines whether orders is literal or an adapter input
for deriving a physical name. For example, two Environment bindings can resolve
this SAME ManifestRevision to staging_orders and staging_shard_orders.
The manifest contains neither a catalog-specific namespace UUID nor an expression
that reads the current Environment during execution. Import remains independent
of Environment and normalized-manifest equality remains unchanged by assignments.
Components reference Resources/logins by their existing stable logical IDs.

### Environment-to-Provider binding owns policy selection

Attach naming configuration to the Environment-to-Provider binding, selected via
its Environment-local alias. Do not store it as a global ProviderInstance property,
application default parameter or PostgreSQL connection option. It must be possible
to choose different naming settings for the same external system in different
Environments and independent Haby catalogs.

Naming settings have immutable exact revisions. The binding selects a precise
revision; there is no saved latest reference. Administrative changes are explicit,
optimistically conditional and atomic. Creating a policy revision alone changes
neither the binding nor any accepted deployment. Policy selection is independent
from the Provider configuration revision, implementation and preflight authority.
It must not become a route around the accepted alias/preflight rules.

Shared storage and operator envelopes use generic typed identities/fields and
versioned validated payloads. Adapter-specific validation and effect interpretation
remain outside shared persistence; add no DatabasePrefix, RabbitMqUserPrefix or
other Provider-specific columns, parallel per-Provider tables or EAV framework.

### Two explicit modes

Initially support these modes:

- exact: an explicitly declared external name is used literally. For generated
  names, the adapter uses its explicit deterministic generation contract.
- prefixed: the adapter adds the configured explicit prefix to the declared or
  generated name according to its closed versioned naming contract.

Mode selection is explicit. Never guess whether a declaration already contains
its prefix, strip or deduplicate a prefix, infer it from Environment display name,
or interpret Liquid in arbitrary Resource properties. A declaration already
starting with the prefix is not an implicit escape from prefixed mode.

The current literal/exact path remains available. Absence/default handling must be
canonical and frozen as exact in the implemented contract, not a mutable execution
time fallback. Concrete JSON/CLI fields, bounds and adapter-specific rules must
be documented and tested together with the implementation.

### Adapter-owned names, shared planning inputs

Adapters decide which named entities the policy affects and validate permitted
characters, length, derived names and external namespace. Do not prepend text to
all strings, Resource IDs, databaseRef references, SecretRefs, passwords or endpoints.
The first production implementation is PostgreSQL databases and generated login
roles. RabbitMQ is a future adapter using the same neutral boundary, not a new
production capability supplied by this decision.

Names must fit external limits before Commit. Do not rely on server truncation,
locale-dependent case changes, random retries or silent shortening. A deliberate
bounded generated-name encoding may reserve room for a stable collision-resistant
owner suffix; its algorithm and version must be explicit and frozen.

Generated names account for stable installation/Environment/Resource ownership
across independent catalogs. Existing persisted UUID owner identities may provide
this separation when proven adequate; this does not mandate another installation
registry merely to concatenate a prefix. Do not use display names, generation,
ApplicationVersion, timestamps, passwords or other credential material as seeds.
Restore preserves the same ownership identity, not a new external owner. A cloned
catalog is not an independently authorized owner of the same external resources.

### Exact Plan, Commit and execution

Plan captures exact Provider/configuration/implementation references, selected
naming-policy identity/revision and semantic inputs, resolved external names and
any versioned derivation information needed by the adapter and current codecs.
Keep Resource provenance logical: a physical name is not the ManagedResourceKey.
Planning creates/reserves no external object and allocates no credential material.

Check/Commit verify relevant binding/policy selection coherently with existing
freshness/ExpectedGeneration rules, including changes from an absent selection.
A significant selection change after planning makes that Plan stale; never silently
replan or recalculate names during Commit. Immutable revision creation alone does
not stale a Plan whose exact selected inputs are unchanged.

Execute, action validation, reservation, confirmed outputs, recovery/evidence checks,
materialization and delivery use exact saved names and inputs. Do not validate a
saved graph against a newly recomputed unprefixed name or current alias policy.
Connection formatting uses the confirmed external database/login names, not logical
manifest strings. Continue transient authorized password resolution; no completed
connection strings or secret-derived identity hashes in snapshots or diagnostics.

### Existing and retained Resources do not relocate

Changing a prefix or selected policy alone never mutates accepted generations or
external resources. An update evaluates managed/retained Resources against their
original confirmed naming inputs and names. If it would resolve a different name,
refuse the unsupported relocation rather than recreate, rename, rotate credentials,
transfer ownership, adopt another resource or make retained state a fresh install.

Explicit supported configuration-only generations remain possible when Resource
names/ownership and adapter compatibility are unchanged. Provider rebinding and
changing existing database names remain unsupported under ADR-045. Keep direct
original provenance, reservations, generated slots and exact old documents/grants
through retries, restart, removal and compatible return.

### Local reservations are not distributed coordination

ADR-035 reservations enforce their existing constraints within one catalog.
Two installations pointing to the same endpoint do not share locks, reservation
rows or automatically recognized ProviderInstance identities. Do not claim global
uniqueness or discover/merge another installation's ownership from an endpoint.

The operator assigns non-overlapping external name spaces. Prefixes alone do not
prove this: staging_ + shard_orders equals staging_shard_ + orders. Validate local
collisions and rely on correct adapter-owned external conflict handling at execution
for other catalogs and out-of-band objects. Never adopt, replace, reset a password
or report success solely because the requested name already exists. External
uniqueness races must fail safely under the existing action uncertainty contract;
this ADR adds no generic recovery or distributed naming service.

Prefixing organizes names; it is not an authorization boundary. PostgreSQL/RabbitMQ
permissions establish actual isolation. An administrative Provider with broad rights
still has broad rights even when a prefix is configured.

## Implementation and required evidence

Use one current alpha model under ADR-033. If planning snapshots, action payloads or
storage change incompatibly, document the fresh-catalog requirement explicitly.
Do not accumulate old-alpha codecs solely for compatibility, silently reset data,
rewrite accepted histories or backfill inferred external ownership.

Before Workload implementation, prove the same imported Application declaration in
two independently bootstrapped catalogs on one disposable PostgreSQL server, with
staging_ and staging_shard_ bindings. Both must create distinct databases/logins,
deliver their exact names and credentials and preserve data across updates/restart.
Cover exact mode, bounded generated names, malformed/oversized prefixes and names,
local and external collisions (including overlapping-prefix examples), policy CAS
and Plan freshness, missing policy/adapter support, and immutable old/retained names.
Use real authenticated Host/generated CLI, Native AOT and real database assertions.
A narrow typed non-PostgreSQL test must prove neutral policy capture/resolution and
persistence dispatch, without implementing RabbitMQ or generic string templating.

The concrete implementation issue must include schema/DTO/snapshot round trips,
strict generated JSON metadata, atomicity and current-build restore evidence where
persisted changes require it, and all existing solution/PostgreSQL/AOT/security gates.
C#/EF workflow and declarative constraints remain required; no stored workflow or
mandatory database-role split. Implementation, not this ADR, completes the feature.
