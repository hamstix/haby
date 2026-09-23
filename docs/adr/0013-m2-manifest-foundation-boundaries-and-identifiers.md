# ADR-013: M2 manifest foundation boundaries and identifiers

- Status: Accepted
- Date: 2026-09-23

## Context

M2.1 replaces the unused prototype model with the first implementation of the
accepted Application manifest domain. Haby needs project boundaries that keep
the new core independent from hosting and persistence, a clear separation
between external JSON and validated domain state, and identifier rules that do
not make adoption depend on globally namespaced or product-derived names.

Existing repositories commonly already have stable lowercase kebab-case names.
Requiring reverse-DNS Application IDs would add migration work, encourage
organization or product names to become permanent identity, and provide little
value to an installation that already owns its Application catalog.

The prototype has no deployed consumers. The new vertical slice can therefore
be introduced alongside the old projects without preserving their contracts or
performing a single large rewrite.

## Decision

### M2.1 introduces three core projects

The first vertical slice adds these production projects:

```text
src/Haby.Domain
src/Haby.Manifest
src/Haby.Application
```

and corresponding focused test projects:

```text
tests/Haby.Domain.Tests
tests/Haby.Manifest.Tests
tests/Haby.Application.Tests
```

Their dependency direction is:

```text
Haby.Domain
    ^
    |
Haby.Manifest
    ^
    |
Haby.Application
```

`Haby.Application` may reference both `Haby.Domain` and `Haby.Manifest`.
`Haby.Manifest` may reference `Haby.Domain` to produce validated domain values.
`Haby.Domain` has no project dependency on either project.

These projects do not reference ASP.NET Core hosting, EF Core, generated gRPC
server contracts, UI packages or plugin implementations. API contracts,
PostgreSQL persistence and the complete plugin SDK are not introduced merely to
create the M2.1 project structure. The prototype projects remain buildable while
covered vertical slices are moved to the new projects incrementally.

### Folder is deferred until after the core administration UI

Folder remains an accepted optional navigation concept, but it is not part of
the M2 or M2.1 implementation boundary. M2 does not introduce a Folder aggregate,
persisted Folder identity, repository, API or manifest reference, and it does not
carry the prototype `OrganizationUnit` into the new core as a placeholder.

Applications exist independently and do not require Folder placement. Folder
persistence, APIs and navigation are scheduled as M5.1 after the base
administration UI and its Application views are stable. The later implementation
must preserve the already accepted invariant that Folder paths do not affect
identity, configuration, authorization, lifecycle or external resource names.

### Manifest DTOs and domain models are different types

Source-generated manifest DTOs represent external JSON. They use strings,
closed concrete collections, explicit discriminators and intentional
`JsonElement` extension points so `System.Text.Json` can deserialize them
without reflection-based type discovery.

Domain models contain validated value objects and normalized declarations. They
cannot represent an unresolved local reference or a value that has failed the
relevant domain invariants merely because such a value was deserializable.

The one-way import pipeline is:

```text
JSON input
  -> syntax and schema checks
  -> source-generated manifest DTOs
  -> semantic validation and reference resolution
  -> validated normalized domain declaration
```

Transport DTOs, manifest DTOs, domain models and future persistence entities are
not one shared object graph. The exact validation-error contract and the rules
for normalized-declaration equality are separate decisions that must be accepted
before their M2.1 implementation.

### ApplicationId is portable, opaque and installation-scoped

`ApplicationId` is the stable logical identifier carried in
`manifest.metadata.id` and in portable Application-version references. It is an
opaque string to consumers: no code may infer a product, organization, folder,
repository or ownership hierarchy from its segments.

An Application ID:

- is lowercase ASCII;
- has at most 128 characters;
- matches `^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$`;
- is unique within one Haby installation;
- is immutable for the lifetime of the Application identity.

Both `automation-processor-service` and `org.example.automation` are valid.
Reverse-DNS naming is a recommendation for publishers who need globally
recognizable IDs; it is not a validation rule. Existing stable kebab-case
repository names can therefore be copied into `metadata.id` during adoption.
After adoption, the manifest is authoritative and Haby never derives the ID from
the current repository, folder or display name.

Changing `ApplicationId` means creating or migrating an identity, not performing
an ordinary rename. An explicit Application-identity migration is outside M2.1.
Display names and folder placement remain mutable.

Manifest-local IDs remain stricter lowercase kebab-case identifiers and are
scoped by their structural owner. For example, several worker Components or
Workloads use separate local IDs; their names are not encoded into
`ApplicationId`.

### Persisted identities use aggregate-specific UUIDv7 value types

Persisted aggregate identities are strongly typed value objects backed by
UUIDv7 values generated when an aggregate is created. Contracts do not expose a
universal `EntityId` type or pass bare `Guid` values across domain and
application boundaries.

The portable logical `ApplicationId` remains distinct from the internal
persisted Application identity. The same distinction applies between portable
Resource and Workload declaration identities and their Environment-specific
managed aggregate identities. UUIDs use their canonical textual representation
when a transport eventually exposes them; they are not part of `haby.json`.

### Manifest revision numbers are local sequences, not identities

Every `ManifestRevision` has its own strongly typed UUIDv7 persisted identity.
It also has a positive 64-bit revision number that increases monotonically
within one Application. The number is convenient for local exact references and
operator-facing history, but it is neither globally unique nor portable between
Haby installations.

Portable release references continue to use the structured pair
`ApplicationId + ApplicationVersion`. A local revision reference uses the
Application identity plus its revision number. `ApplicationVersion` remains a
producer-supplied opaque string; Haby does not infer ordering from it.

## Consequences

- Existing kebab-case service names can become Application IDs without a
  namespace migration.
- Organization and product renames do not force Application identity changes.
- Public publishers may still adopt reverse-DNS naming when collision avoidance
  outside a single installation is valuable.
- Malformed input remains representable only in DTOs and validation results, not
  as accepted domain state.
- Database and transport mappings must explicitly convert aggregate-specific ID
  value types.
- The old prototype can remain operational during small M2 pull requests, but it
  does not constrain new contracts.
- M2 implementations do not need speculative Folder abstractions; Folder support
  is added as a complete UI-oriented vertical slice in M5.1.

## Rejected alternatives

### Require reverse-DNS Application IDs

This complicates migration and permanently embeds organization or product
naming into identity. It remains optional guidance rather than a requirement.

### Derive ApplicationId from a repository or folder

Repositories, products and folder placement can be renamed. Derivation would
turn routine organizational changes into identity changes and would expose
producer provenance across the delivery boundary.

### Reuse manifest DTOs as domain or persistence models

This would allow partially validated external state to leak into business logic
and couple the public JSON shape to storage and domain evolution.

### Use one generic ID type or bare Guid values

This permits accidental comparison or substitution of unrelated aggregate IDs
and hides important scope distinctions from APIs and tests.

## Follow-up work

- accept stable validation-error codes, paths and aggregation rules;
- accept normalized-manifest equality and canonical fingerprint boundaries;
- implement the three projects and tests as the first M2.1 vertical slice;
- define explicit identity-migration behavior only when a real rename use case
  requires it.
