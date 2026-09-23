# ADR-014: Manifest validation diagnostics

- Status: Accepted
- Date: 2026-09-23

## Context

M2.1 validates manifests through several layers: JSON parsing, the core JSON
Schema, source-generated DTO deserialization, core semantic rules and
type-specific `properties` schemas. CLI, future REST and gRPC APIs, the web
editor and tests need the same machine-readable result without depending on a
particular parser, validation library or transport.

Validation diagnostics can contain user-controlled values and can point into
fields that hold credentials or other sensitive configuration. The contract
must therefore be deterministic and useful without copying arbitrary manifest
content into errors.

Successful imports may also produce warnings. Because a ManifestRevision is an
immutable historical record, reading it later must not silently reinterpret its
original validation result using newer rules.

## Decision

### Validation has one transport-neutral semantic model

Domain and application code use a transport-neutral validation report and
diagnostic model. Manifest DTOs, domain types, REST DTOs and Protobuf messages
remain different types and map explicitly at their boundaries.

A validation report contains:

- `IsValid`, which is true when no diagnostic has `Error` severity;
- an ordered collection of diagnostics;
- `Truncated`, which states that the diagnostic limit was reached.

A manifest diagnostic contains:

- `Code`: a stable machine-readable code;
- `Severity`: `Error` or `Warning`;
- `Path`: an RFC 6901 JSON Pointer relative to the root of `haby.json`;
- `Message`: an English human-readable description that is not a stable machine
  contract;
- `Parameters`: optional safe string parameters for presentation and tests;
- `RelatedPaths`: optional JSON Pointers for other declarations participating in
  the same problem;
- `Location`: an optional one-based line and column, primarily for JSON syntax
  errors that cannot provide a useful JSON Pointer.

The empty JSON Pointer identifies the manifest root. Manifest diagnostic paths
never include the import-envelope `/manifest` prefix, so local validation, CI,
REST, gRPC and the future editor produce the same paths. Validation of
`applicationVersion` or other envelope fields belongs to the import use case,
not to the manifest report.

`Message` may improve between releases. Clients make decisions from `Code`,
`Severity`, `Path` and typed parameters rather than parsing message text.

### Codes are owned by Haby during M2

M2 exposes only standard Haby diagnostic codes. Plugins and type-specific schema
validators do not create arbitrary namespaced code families. Haby maps their
schema results to standard codes such as
`manifest.properties.schema-violation` and preserves the precise manifest path.

Initial code families include:

```text
manifest.syntax.*
manifest.schema.*
manifest.id.*
manifest.reference.*
manifest.type.*
manifest.properties.*
manifest.document.*
manifest.resource.*
manifest.workload.*
```

Examples include `manifest.syntax.invalid-json`,
`manifest.syntax.duplicate-property`, `manifest.schema.required`,
`manifest.schema.invalid-discriminator`, `manifest.id.invalid`,
`manifest.reference.not-found`, `manifest.type.unknown` and
`manifest.resource.unused`.

Codes and their parameter names are public compatibility contracts. Adding a
code is compatible; removing a code, changing its meaning or changing required
parameters requires contract-version consideration. A future plugin SDK may add
a controlled extension-code namespace through a separate decision after a real
use case exists.

### Validation proceeds in dependency order

The import-time pipeline is:

```text
JSON syntax and duplicate-property checks
  -> core JSON Schema validation
  -> source-generated DTO deserialization
  -> core semantic validation and reference resolution
  -> type-specific properties-schema validation
```

A later stage runs only when the preceding stages produced a sufficiently valid
model. This prevents cascades such as unresolved-reference errors caused only by
a missing `spec`. Within a stage, Haby collects independent diagnostics where it
can do so safely.

Diagnostics have deterministic ordering by path, code and related paths.
Parameters use deterministic key ordering when serialized. M2.1 returns at most
100 diagnostics and sets `Truncated` when more diagnostics exist. An invalid
import stores no ManifestRevision.

### Successful-import warnings are immutable historical data

Warnings produced during successful import validation are persisted with the
immutable ManifestRevision as historical validation diagnostics. They are never
recomputed implicitly when that revision is read.

An explicit revalidation operation may evaluate an existing revision using the
current rules. It creates a separate revalidation report and never updates the
accepted declaration, the original warnings or other ManifestRevision data.
Whether a later policy uses that report to restrict a future operation is a
separate decision; revalidation alone does not rewrite history.

Only successful-import warnings are stored with a ManifestRevision. Errors from
a rejected import remain part of that operation result and do not create a
revision.

### Manifest diagnostics and application errors remain separate

A diagnostic describes the declaration being validated. Authentication failure,
request-size limits, optimistic-concurrency failure and an
`ApplicationId + ApplicationVersion` content conflict are application or
transport errors rather than manifest diagnostics. In particular, a declaration
that conflicts with an already imported version may be valid under a new
Application version.

### Diagnostics never disclose sensitive values

Messages, parameters and related paths may contain identifiers, property names,
expected types and references. They must not contain secret values, complete
configuration fragments, credentials or arbitrary `properties` content. Haby
reports the path to a sensitive invalid value without echoing that value.

### REST and gRPC are adapters over the same semantics

The validation model is suitable for both public transports:

- a dedicated REST or gRPC validation operation returns a validation report as a
  normal successful transport response even when `IsValid` is false;
- an import operation can expose the same diagnostics in a typed validation
  failure;
- REST status/problem mapping and gRPC status-detail mapping are defined with the
  public API in M4 and do not enter the M2.1 domain projects.

Source-generated JSON DTOs and Protobuf messages use closed severity enums,
strings, string maps, repeated paths and an optional source location. They do not
serialize exceptions, validation-library objects or arbitrary plugin payloads.

## Consequences

- CLI, API and UI clients can highlight the same path and react to the same code.
- M2.1 tests can assert deterministic reports without asserting exception text.
- Historical warnings remain reproducible even after validators evolve.
- Explicit revalidation is observable without mutating an immutable revision.
- Type-specific schemas remain extensible without making plugin-defined error
  taxonomies part of the first public contract.
- REST and gRPC adapters require explicit DTO mapping but do not duplicate
  validation behavior.

## Rejected alternatives

### Return only strings or exceptions

Strings are unsuitable for stable automation, field highlighting, aggregation
and localization. Exceptions expose implementation details and map poorly across
transports.

### Use transport-specific validation as the application contract

ASP.NET Core validation types, `ProblemDetails` or generated Protobuf types would
couple M2.1 validation to hosting and make CLI and in-process use less reliable.

### Recompute warnings whenever a revision is read

The visible history would change after upgrades even though the immutable
revision did not. Revalidation must instead be explicit and separately recorded.

### Allow arbitrary plugin diagnostic codes in M2

This would create an ungoverned compatibility surface before plugin validation
requirements are known. Standard Haby codes cover the initial schema boundary.

## Follow-up work

- define the initial code catalog and required parameters in implementation;
- define normalized-manifest equality and canonical fingerprint boundaries;
- map reports to versioned REST and gRPC contracts in M4;
- decide persistence and retention for explicit revalidation reports when that
  use case is implemented.
