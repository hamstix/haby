# ADR-021: Application manifest validation and import use cases

- Status: Accepted
- Date: 2026-10-14

## Context

ADR-020 defines the versioned JSON envelope and the deterministic parsing,
validation and normalization pipeline. ADR-014 defines manifest diagnostics,
and ADR-015 requires one immutable declaration for each
`ApplicationId + ApplicationVersion`. M2.1 still needs an application boundary
that composes those decisions without making transport adapters, persistence or
provider execution part of manifest import.

The boundary must retain raw JSON semantics long enough to reject duplicate
members, distinguish request errors from manifest diagnostics, make repeated
imports idempotent and resolve concurrent imports without replacing an accepted
revision. Expected validation and version-conflict outcomes must remain usable
by in-process code and future REST and gRPC adapters without relying on
exceptions or database-specific behavior.

## Decision

### Validation and import are separate use cases

M2.1 exposes two application use cases:

```text
ValidateApplicationManifest
ImportApplicationManifest
```

Both use the same parsing, validation and normalization pipeline. Validation is
strictly read-only. Import is the only use case that may create an `Application`
and an immutable `ManifestRevision`.

Both accept the complete import envelope defined by ADR-020. There is no second
input contract that duplicates `ApplicationId`, `apiVersion`, a client digest or
other manifest members outside that envelope.

### The entry boundary consumes the original UTF-8 JSON payload

The initial Manifest-layer boundary accepts the original UTF-8 JSON payload,
not an object that a transport adapter has already deserialized. This allows the
shared implementation to detect:

- a byte-order mark or invalid UTF-8;
- malformed JSON;
- duplicate properties in the envelope or manifest;
- excessive JSON nesting;
- unknown envelope properties;
- invalid envelope member types.

The envelope parser belongs to `Haby.Manifest`. A REST controller, gRPC adapter,
CLI or test must not deserialize the envelope in a way that loses duplicate
properties before invoking the use case. Metadata-based source-generated DTO
deserialization starts only after the syntactic and envelope checks succeed.

Transport request-size limits remain configurable outside the manifest domain.
An adapter stops reading once its configured limit is exceeded and maps the
outcome to the standard request-too-large application error.

### Envelope errors and manifest diagnostics are distinct

Failures in the envelope use stable application-level codes:

```text
manifest-import.invalid-encoding
manifest-import.invalid-json
manifest-import.duplicate-property
manifest-import.required
manifest-import.unknown-property
manifest-import.type-mismatch
manifest-import.invalid-application-version
manifest-import.request-too-large
```

Their JSON Pointer paths are relative to the envelope, for example
`/applicationVersion` or `/manifest`. Once the `manifest` value has been
identified, violations inside it use the accepted `manifest.*` diagnostic
catalog and paths relative to the manifest root, without a `/manifest` prefix.

Request errors follow the same redaction principle as manifest diagnostics:
they describe the violated request contract and may include bounded safe
property or expected-type information, but never echo an invalid value,
arbitrary JSON, parser exception text or secret material.

### Validation has a closed result contract

The transport-neutral validation result is a discriminated union equivalent to:

```text
ValidateApplicationManifestResult
├── Valid
│   ├── ApplicationId
│   ├── ApplicationVersion
│   └── Warnings
├── InvalidRequest
│   └── RequestErrors
└── InvalidManifest
    └── ValidationReport
```

The public result does not need to expose the normalized internal model. The
validated normalized candidate remains an internal value passed from the shared
pipeline to the import orchestration.

### Import has a closed result contract

The transport-neutral import result is a discriminated union equivalent to:

```text
ImportApplicationManifestResult
├── Succeeded
│   ├── ManifestRevisionRef
│   ├── Disposition
│   │   ├── Created
│   │   └── ExistingEquivalent
│   └── HistoricalWarnings
├── InvalidRequest
│   └── RequestErrors
├── InvalidManifest
│   └── ValidationReport
└── ApplicationVersionContentConflict
```

`Created` and `ExistingEquivalent` are distinct successful dispositions. This
allows automation to report whether it created a revision without treating an
idempotent repeat as an error.

Expected outcomes are returned as typed results. Cancellation, infrastructure
failure and detection of corrupt persisted state remain exceptional conditions.

### The first accepted revision creates its Application atomically

The first successful import of an `ApplicationId` atomically creates the stable
catalog `Application` and its first `ManifestRevision`. A rejected import never
leaves an empty Application record.

In M2.1 the Application record owns stable catalog identity. It does not contain
a mutable implicit current manifest, current labels or current display metadata.
Complete declaration metadata belongs to each immutable ManifestRevision. A
future listing or search projection may present metadata from a selected
revision, but that projection is not another source of truth.

An Application-local revision number starts at `1` and increases monotonically.
Gapless numbering is not a domain invariant: rollback or contention may leave a
gap, but an allocated number is never reused.

Persistence enforces two independent uniqueness constraints:

```text
ApplicationId + ApplicationVersion
ApplicationRecordId + LocalRevisionNumber
```

The first pair is the portable version identity. The second pair forms the
local exact revision reference.

### Import uses an atomic compare-and-create persistence port

Application code must not implement import as an unsafe uncoordinated
`find -> insert` sequence. It uses a persistence-neutral import store or
repository port that can atomically create the candidate or report a revision
created by a concurrent importer.

Before invoking that port, the shared pipeline produces a fully validated
immutable candidate equivalent to:

```text
ValidatedManifestCandidate
├── ApplicationId
├── ApplicationVersion
├── InputApiVersion
├── NormalizedModelVersion
├── NormalizedDeclaration
├── HistoricalWarnings
└── ImportAuditMetadata
```

Environment, Provider alias resolution, external Resource-export resolution and
external state are not import inputs.

When two equivalent candidates race, one result is `Created` and the other is
`ExistingEquivalent`; both contain the same `ManifestRevisionRef`. When two
different candidates race under the same Application version, one succeeds and
the other returns `ApplicationVersionContentConflict`. The losing candidate
never creates another revision or mutates the winner.

M2.1 implements this boundary with an in-memory store and focused concurrency
tests. EF Core entities, PostgreSQL transactions and migrations belong to a
later persistence slice and must preserve the same semantics.

### Normalized comparison is authoritative

The normalized declaration comparison accepted by ADR-015 remains the
authoritative equality test. A server-generated fingerprint may accelerate
lookup and rejection, but a matching digest is followed by normalized-content
comparison.

If implemented in M2.1, the fingerprint is a typed value containing:

```text
ManifestFingerprint
├── NormalizationProfile
├── Algorithm: sha256
└── Digest
```

It is not accepted from a client, is not a physical-file digest and is not a
`ManifestRevisionRef`.

### Version-content conflicts expose paths, not values

The conflict result contains only:

```text
ApplicationVersionContentConflict
├── ApplicationId
├── ApplicationVersion
├── ExistingManifestRevisionRef
├── ChangedPaths
└── ChangedPathsTruncated
```

Changed paths are ordinally sorted and limited to 100 entries. The result never
contains old or new values, complete fragments or secret material.

The normalized diff follows these rules:

- different scalar values produce the scalar path;
- an added or removed Object member produces that member's path;
- Objects are compared recursively;
- a changed ordered Array produces the Array path as one change;
- a changed node kind produces that node's path;
- `$schema`, import audit metadata, historical warnings and fingerprints do not
  participate.

Treating a changed Array as one path keeps the public conflict contract stable
without promising element-level edit semantics.

### Historical warnings and first-import metadata remain immutable

Warnings are stored only when a revision is first created. An equivalent repeat
returns the existing revision's historical warnings. It does not replace the
first import time or actor, rewrite warnings or implicitly revalidate the stored
revision under current rules.

The Validate use case, and a future explicit revalidation use case, may report
diagnostics produced by current rules without mutating revision history. M2.1
does not create a separate audit event for an equivalent repeated import.

Import audit metadata comes from trusted execution context outside the JSON
envelope and contains at least:

```text
ImportAuditMetadata
├── ActorId
└── ImportedAt
```

`ActorId` is a stable bounded identity prepared by the composition root. Source
repository, commit, pipeline and local file path remain producer-private and are
not imported. `ImportedAt` is generated in UTC by an injected clock; a client
cannot provide or override it.

### Cancellation has a commit boundary

Before the atomic commit, cancellation may end the operation without creating a
revision. Once the import store confirms the commit, the use case treats the
operation as successful. Cancellation must not turn an already committed
revision into a reported validation failure.

`InvalidRequest`, `InvalidManifest`, `ExistingEquivalent` and
`ApplicationVersionContentConflict` are ordinary application outcomes and are
not logged as internal failures.

### Import remains side-effect free outside its catalog state

A successful import does not create or select any of the following:

- `ApplicationDeployment`;
- `DeploymentChangeSet`;
- `DeploymentPlan`;
- Resource or ManagedWorkload instances;
- rendered or published Configuration documents.

Those actions require later explicit use cases.

## Required M2.1 tests

The first implementation proves at least that:

- an invalid request creates no Application;
- an invalid manifest creates no Application;
- the first valid import creates an Application and local revision `1`;
- an equivalent repeat returns the same revision reference;
- different content under the same version returns a conflict;
- a different version creates a revision even when content is equivalent;
- concurrent equivalent imports create one revision;
- concurrent different imports preserve the winning declaration;
- repeated import does not change historical warnings or first-import metadata;
- conflict paths are deterministic, bounded and disclose no values;
- Validate never mutates state.

## Consequences

- CLI, future REST and future gRPC adapters can share exactly one import
  implementation and result model.
- The raw payload must reach the Manifest layer before ordinary DTO binding.
- Import behavior can be tested in memory before choosing PostgreSQL entity and
  transaction shapes.
- Application-version immutability is protected under contention as well as in
  sequential tests.
- Read models must select revision metadata explicitly instead of relying on an
  import-order-dependent current manifest.

## Rejected alternatives

### Let each transport deserialize and validate its own request

This loses duplicate-member information, produces adapter-specific errors and
makes CLI, REST and gRPC behavior drift.

### Return one generic failure or throw for expected outcomes

Clients need to distinguish request shape, manifest validation, idempotent
success and immutable-version conflict without parsing messages or exception
types.

### Create an Application before validating its first manifest

This leaves empty catalog records after invalid submissions and makes the
aggregate lifecycle depend on failed requests.

### Maintain a mutable current manifest on Application

Application versions are opaque and have no ordering semantics. Import order is
not a valid desired-state selection policy; Environment-specific selection
belongs to ApplicationDeployment.

### Persist every repeated-import attempt in the revision

That would mutate immutable revision history. General audit events can record
attempts later without changing the ManifestRevision.

### Report element-level Array diffs

It would add edit-script semantics and stability expectations that are not
needed to explain an immutable-version conflict.

## Follow-up work

- implement raw-envelope parsing and the source-generated DTO pipeline in
  `Haby.Manifest`;
- implement the application result unions, validated candidate and in-memory
  atomic import store;
- add the sequential and concurrent import acceptance tests listed above;
- map request errors and results to versioned REST and gRPC contracts in M4;
- implement PostgreSQL uniqueness and transaction guarantees in the persistence
  milestone;
- add explicit revalidation and general audit-event retention only when those
  use cases are introduced.
