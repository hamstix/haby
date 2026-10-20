# ADR-020: Versioned Application manifest JSON contract

- Status: Accepted
- Date: 2026-10-13

## Context

M2.1 needs one deterministic input contract for `haby.json`, its import
envelope, the checked-in JSON Schema and metadata-based `System.Text.Json`
source generation. The contract must work equally through future REST and gRPC
adapters, reject ambiguous JSON before deserialization and preserve accepted
Manifest revisions even after an alpha input API adapter is retired.

Haby imports a structured declaration rather than an exact physical file. File
formatting, property order and source bytes are therefore not portable identity.
Application version, private producer provenance and Environment-specific state
also remain outside the manifest declaration.

## Decision

### M2.1 accepts a JSON manifest through a minimal envelope

The recommended repository file is `haby.json`. M2.1 accepts Application
manifests as JSON only. YAML remains an output format for structured
Configuration documents and is not an alternative manifest syntax.

The transport-independent import DTO has exactly two members:

```json
{
  "applicationVersion": "3.4.6-rev.1042",
  "manifest": {
    "$schema": "https://haby.dev/schemas/application-manifest.v1alpha1.json",
    "apiVersion": "haby.dev/v1alpha1",
    "kind": "Application",
    "metadata": {
      "id": "automation-processor-service"
    },
    "spec": {}
  }
}
```

`ApplicationId` is read only from `manifest.metadata.id`. The envelope does not
duplicate it and does not carry a client digest, repository URL, commit, source
path or other producer-private provenance. Transport adapters may add trusted
request context outside this public DTO.

### Application versions are bounded opaque identifiers

`applicationVersion` is non-empty ASCII, has at most 128 characters and matches:

```regex
^[A-Za-z0-9](?:[A-Za-z0-9._+-]{0,127})$
```

Haby compares it by exact ordinal equality. Haby does not require SemVer, parse
ordering components or decide which version is newer. Values such as
`3.4.6-rev.1042`, `2026.10.13` and `1.0.0+build.17` are valid.

### The manifest header selects one exact contract

`apiVersion`, `kind`, `metadata`, `metadata.id` and `spec` are required and
non-null. In the first contract, `apiVersion` is `haby.dev/v1alpha1` and `kind`
is `Application`. Property names and discriminator values are case-sensitive;
the parser does not infer a document kind or try another version from its shape.

`$schema` is an optional authoring hint. When present, it must equal the
canonical schema URI associated with the selected `apiVersion`. A mismatch is a
manifest validation error. `$schema` remains excluded from normalized equality.

Published manifest API versions are immutable while they are supported. Haby
may support multiple input API versions through version-specific parsing,
validation and normalization adapters. All supported versions are converted
into a common internal model. Alpha versions carry no long-term compatibility
guarantee and may be removed before the stable manifest contract is released.

An unsupported `apiVersion` produces a standard bounded diagnostic at
`/apiVersion`. Haby does not fall back to the newest version, downgrade the
document or guess a compatible schema. The same fail-closed rule applies to an
unknown `kind`.

### JSON parsing is strict and deterministic

The input is UTF-8 JSON without a byte-order mark. Comments, trailing commas,
malformed Unicode, `NaN`, `Infinity` and numeric strings used in place of JSON
numbers are rejected. The domain parser enforces a maximum nesting depth of 64;
transport request-size limits remain independently configurable.

Duplicate member names are rejected in every JSON object before DTO
deserialization, including objects inside explicit extension boundaries. Haby
never applies last-value-wins semantics.

Core DTOs reject unknown properties recursively. Arbitrary JSON is permitted
only at explicit typed extension boundaries:

- Resource and Workload `properties`;
- Binding `options`;
- structured Configuration document templates;
- typed literal parameter defaults.

Those values use `JsonElement`, never `object`, and are validated by the exact
registered contract where applicable. An extension boundary is not a mechanism
for choosing a CLR type.

Exact numeric constraint values in parameter definitions are core typed fields,
not additional `JsonElement` extension boundaries. Their wire representation
and converter are specified by ADR-019.

### Absence, null and collection defaults are explicit

Absent and explicit `null` are different unless a field contract explicitly
states otherwise. Required members must be present and non-null. Optional scalar
members may be absent, but explicit `null` is invalid unless a future version
explicitly permits it.

The following absent collections materialize as empty core defaults:

- `metadata.labels`;
- `spec.parameters`, `spec.resources`, `spec.exports` and `spec.components`;
- Component `bindings`, `documents` and `workloads`;
- optional projection collections whose field contract defines an empty default.

Omission and an explicit empty collection are therefore equal after core
normalization. An Application may contain no Components. This supports an
infrastructure-only Application that owns Resources and may expose them through
Resource exports for Components in other Applications.

Documented member defaults also apply inside a present empty object. In
particular, absent Resource `lifecycle`, `lifecycle: {}` and an explicit
`deletionPolicy: "retain"` normalize identically. Explicit `null` is invalid,
and closed core objects still reject unknown members.

Manifest-local declarations use map keys as their IDs. An entry does not repeat
that ID in its value, and JSON object member order has no semantic meaning.
Source-generated DTOs must therefore accept a union discriminator even when it
appears after other members of the same JSON object. Required DTO members must
enforce presence as well as type; non-nullable CLR annotations alone do not
provide that guarantee. The schema and DTO corpus covers both rules.

### Validation precedes source-generated deserialization

The deterministic import pipeline is:

```text
UTF-8 JSON
  -> syntax, duplicate-member and depth validation
  -> import-envelope validation
  -> exact apiVersion and kind dispatch
  -> version-specific JSON Schema validation
  -> metadata-based source-generated DTO deserialization
  -> core semantic validation
  -> registered extension-contract validation
  -> normalization
```

A successful complete-validation result requires every applicable stage,
including registered extension-contract validation, to have run without
errors. Core-schema and semantic success alone must not be exposed as a
complete `Valid` result or used to admit a normalized ManifestRevision.

The typed import-envelope DTO is part of the source-generated M2.1 contract.
Its embedded `manifest` JSON value is dispatched by exact `apiVersion` and
`kind` before version-specific DTO deserialization; it is not an additional
manifest extension boundary. Slice 2 defines this DTO. Slice 3 implements raw
UTF-8 intake, envelope validation and the dispatch pipeline.

Schema validation remains authoritative for complete aggregated shape
diagnostics; DTO deserialization is not used as the only validator. All manifest
DTOs use camel-case, case-sensitive metadata-based `System.Text.Json` source
generation with explicit closed discriminator sets and no reflection-based type
discovery or shape-guessing converters.

Envelope failures such as an invalid `applicationVersion`, a missing `manifest`,
an unknown envelope member or a transport-size violation are import-request
errors. Diagnostics inside `manifest` use pointers relative to the manifest,
for example `/metadata/id`, rather than `/manifest/metadata/id`.

### The checked-in schema is a public versioned artifact

M2.1 uses JSON Schema Draft 2020-12. The first schema is checked in as:

```text
schemas/application-manifest.v1alpha1.json
```

It declares:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://haby.dev/schemas/application-manifest.v1alpha1.json"
}
```

Core objects use closed properties and explicit `$defs` and `oneOf` branches.
The schema is not generated dynamically at runtime.

The checked-in schema and source-generated DTOs are peer representations of one
contract. A shared positive and negative test corpus must detect drift between
schema validation, DTO deserialization and semantic validation. It includes
valid manifests, invalid syntax, duplicate and unknown members, wrong casing,
missing required members, nulls, unknown discriminators and extension-contract
examples.

The exact required members, defaults, lexical constraints, discriminated shapes
and cross-field rules are fixed by the
[`haby.dev/v1alpha1` contract](../architecture/application-manifest-v1alpha1.md).
The checked-in schema, DTOs and normalized mapper must implement that field
matrix without adding independent defaults or permissive alternatives.

Validation remains bounded by the diagnostic limit accepted in ADR-014. Limits
on individual identifiers and values belong to their versioned field schemas;
the manifest domain does not hard-code an HTTP or gRPC request-size limit.

### Accepted revisions are independent from retired input adapters

Haby does not promise to preserve submitted JSON bytes, formatting or member
order. A ManifestRevision retains its normalized representation and does not
require the original manifest API adapter for planning or reconciliation.

The persisted revision records at least two independent versions:

```text
InputApiVersion: haby.dev/v1alpha1
NormalizedModelVersion: 1
```

`InputApiVersion` identifies the producer-facing contract that accepted the
declaration. `NormalizedModelVersion` identifies the persisted internal
representation used by current planning and reconciliation code.

That persisted representation must reconstruct the typed normalized domain
graph defined by ADR-013. A versioned serialized or canonical tree may be used
as its storage encoding, but a generic tree is not the authoritative runtime
declaration and must not require consumers to reinterpret core manifest member
names. The input DTO and its version-specific adapter are not needed to read an
accepted revision.

An internal normalized-model change requires a deterministic migration or
upcaster. It must preserve the accepted declaration's semantics, revision
identity and recorded normalized fingerprint; runtime code must not reconstruct
the state by passing a stored revision through its original input adapter.

When returned through an API, the declaration may be represented as normalized
JSON and need not reproduce the physical input file.

## Consequences

- CLI, REST and gRPC adapters share one transport-neutral import use case.
- Parser behavior is deterministic across casing, duplicate members, nulls and
  defaulted collections.
- Checked-in schemas can drive editor support without becoming runtime-generated
  metadata.
- A Haby release can retire an unsupported alpha input adapter without breaking
  planning or reconciliation for previously accepted revisions.
- Infrastructure-only Applications are expressible without inventing a dummy
  Component.
- Schema, DTO and normalization changes require explicit versioning and corpus
  tests rather than silent compatibility assumptions.

## Rejected alternatives

### Accept both JSON and YAML manifests in M2.1

Two input syntaxes add duplicate-member, scalar-typing and source-location rules
before the first public contract is implemented. YAML remains available where
it provides value: rendered Configuration documents.

### Treat application versions as SemVer

Producer build versions such as `3.4.6-rev.1042` are portable identities, not a
request for Haby to implement release ordering or range resolution.

### Preserve unknown core properties for forward compatibility

Ignoring or round-tripping unknown members can silently accept misspellings and
leaves older servers unable to validate semantics they do not understand. A new
public contract is selected explicitly through `apiVersion`.

### Use deserialization failures as the validation report

Deserializer exceptions are incomplete, transport-specific and commonly stop
at the first failure. The schema-first pipeline produces stable aggregated Haby
diagnostics.

### Retain every alpha adapter forever

That would turn pre-stable experiments into permanent compatibility obligations.
Accepted revisions remain usable because normalized state is persisted
independently from the input adapter.

### Reparse accepted source documents during reconciliation

This would make deployed state depend on whether an old adapter is installed and
could silently reinterpret a revision after an upgrade.

## Follow-up work

- implement the shared raw-envelope parser, source-generated import-envelope and
  version-specific manifest DTOs through the use cases accepted in
  [ADR-021](0021-application-manifest-validation-and-import-use-cases.md);
- add the Draft 2020-12 schema and shared positive and negative corpus;
- implement duplicate-member, strict UTF-8 and maximum-depth checks before DTO
  deserialization;
- define the normalized persisted model version and its first mapper;
- expose transport adapters only after they can preserve the same diagnostics
  and import semantics.
