# First configuration composition contract

- Status: Accepted for implementation
- Date: 2026-11-05
- Authority: ADR-017/018/033/035/036
- Scope: Pure static JSON composition and redacted preview, without I/O

## Trusted semantic values

Extend ConfigurationValue with immutable ConfigurationDeferredValue containing
ConfigurationFormatterId and ConfigurationObject Arguments. Formatter identity
is a distinct typed category using the existing exact versioned contract-ID
rules: `<contract-name>/<contract-version>`. It is independent of renderer,
Resource, assembly and CLR identities. The complete contract name is opaque.

Arguments support existing scalar, Object and Array values and exact
ConfigurationSecretReference nodes. Reject DeferredValue anywhere inside
Arguments; nested formatting/chains are not supported. Equality and hashing use
exact formatter identity and structural Arguments, never resolved secret bytes.
Copies cannot retain mutable caller collections.

This is a trusted internal node, not a manifest JSON alternative. JSON objects
that resemble secret/deferred nodes remain ordinary Objects. Do not extend the
Application schema or its mapper to manufacture either confidential node kind.

Register exact formatter metadata explicitly at build-time. The initial result
kind is String. Validate the Arguments against its registered contract before
accepting a composition result. Unknown contracts and invalid Arguments refuse;
multiple active implementations for the same exact ID are a startup ambiguity
error. Contract definition and implementation registration stay distinct under
ADR-018. No reflection discovery or runtime plugin loading.

Formatter-specific host/timeout/escaping semantics belong to adapters. The common
layer understands values and references only. This slice provides the validation
seam and test registrations, not a PostgreSQL formatter or formatter execution.
Do not resolve secrets, require SecretStore access or introduce an executable
formatting context. Delivery will authorize that capability separately.

## Pure composition

Input is a static template ConfigurationValue plus an immutable collection of
ready projections. Each projection supplies a canonical absolute non-root JSON
Pointer, a ConfigurationFragment and safe origin identity. Fragments contain
ConfigurationValue trees, including trusted confidential nodes. Do not resolve
Bindings, Provider aliases, external outputs or profiles here.

Reuse ADR-017 pointer and collision semantics:

- Create missing Object parents; preserve existing template members.
- Reject an existing leaf, equal paths, ancestor/descendant overlap and traversal
  through an Array, scalar or confidential node. Do not merge or overwrite.
- With projections, the template root must be Object. Without projections any
  supported root value is allowed.
- The empty pointer is forbidden as a projection target. `/` targets the Object
  member with an empty name. Numeric and `-` segments remain Object keys.
- Pointer escaping is RFC 6901; compare canonical paths ordinally. Input
  enumeration order cannot choose winners or change diagnostics/results.

Return either the complete immutable tree or safe diagnostics, never a partially
composed document. Validate the complete input set before publishing success.
No mutation of templates, fragments or caller collections; no database, network,
provisioning or secret resolution. The composer accepts already static semantic
inputs; it does not implement Liquid/parameter execution or claim that an
Application is ready for materialization. The later application pipeline must
refuse unsupported requested evaluation under ADR-036.

Use a separate render-diagnostic result, not historical import warnings. Preserve
the established diagnostic conventions: stable standard codes, canonical paths,
safe identity parameters, deterministic ordering/deduplication and bounded reports
whose validity cannot hide an omitted error. Paths identify document locations;
safe origin identities may distinguish sources. Never include template values,
fragment values, Arguments or exception/driver text in diagnostics. Document the
small concrete render-code set with the implementation; this does not add plugin
defined codes or change the manifest-validation catalog.

## JSON and preview

Ordinary document JSON serialization refuses a tree containing any unresolved
SecretReference or DeferredValue. It must not substitute null, a marker, a
reference object or formatted text. Serialization failure publishes no partial
output. For ordinary trees preserve exact arbitrary-precision JSON numbers and
deterministic ordinal Object member ordering; do not route numbers through
double/decimal. Define deterministic escaping and number spelling in tests and
documentation without claiming this is the manifest equality/fingerprint codec.

Preview is a distinct response, not an application-ready document or persisted
semantic tree:

```json
{
  "document": {
    "PostgreSQL": {
      "DefaultConnection": { "ConnectionString": "***REDACTED***" }
    }
  },
  "redactedPaths": ["/PostgreSQL/DefaultConnection/ConnectionString"]
}
```

Replace every SecretReference and complete DeferredValue with the marker, even
when its Arguments contain no SecretReference. Do not traverse/expose its
Arguments, formatter results or SecretRef identities in preview. Return distinct
canonical redacted paths in ordinal order. A confidential root uses the empty
pointer in redactedPaths; that does not permit root projection. A literal marker
String remains ordinary content with no redacted path.

Use explicit source-generated metadata for production DTO serialization; a
purpose-built Utf8JsonWriter tree writer needs no reflection metadata. No
reflection fallback or polymorphic CLR-name encoding. Preview is not a new
public RPC, authorization bypass, reveal option or confidential delivery API.

## Evidence and boundaries

Tests must prove api/worker fragments share one exact SecretRef but carry
different permitted consumer Arguments and produce distinct trees with both
confidential strings redacted. Use synthetic adapter contracts; PostgreSQL
option names/types/security policy remain a later adapter decision.

Include a second non-PostgreSQL contract through the same actual common seam.
Prove immutable Arguments/fragments/results, equality/hash agreement, nested
DeferredValue refusal, unknown/invalid/ambiguous formatter handling, collision
and pointer boundaries, input-order independence, no partial success, recursive
redaction in Objects/Arrays/root, literal-marker distinction and ordinary JSON
refusal. Exact-number cases include values outside decimal/double range.

No persistence/migrations, ConfigurationDocumentRevision acceptance, binding or
output retrieval, renderer execution, Profile CRUD/assignment/expansion, Provider
option schemas, Liquid, YAML/text execution, confidential delivery, Host/CLI
changes or generation/provisioning changes. No new package is required by this
decision. Next, specify minimal Profile/assignment wire contracts and feed their
validated expansion into this composer; later connect exact generation outputs,
document persistence and operator preview before authorized delivery.

## Implemented in-process API

`ConfigurationComposer` in Haby.Application accepts a static semantic template
and ready `ConfigurationProjection` values. Each projection snapshots its path,
immutable `ConfigurationFragment` and `ManifestLocalId` source identity; composition
copies the supplied projection collection. Successful results retain only immutable
tree nodes. No manifest expression is evaluated by this API. Callers must explicitly
materialize supported inputs before calling it, and refuse unsupported evaluation.

`ConfigurationFormatterCatalog` receives definitions separately from active
implementation identities. Adapter definitions derive immutable records from
`ConfigurationFormatterContract`; include all schema/semantic metadata in record
equality and implement pure `ValidateArguments(ConfigurationObject)` using typed
semantic decoders. Do not use mutable fields, external state, secret resolution,
serialization to fake JSON or formatter execution. Equivalent records are idempotent;
different records for one exact ID conflict. Duplicate active registrations are
ambiguous even if their identities match. Active implementations without definitions
are invalid. Definitions without an active implementation remain known but unavailable.
The only current result kind is String. There are no production formatter registrations.

Validation traverses templates and fragments recursively, including Arrays. A deferred
node validates its Arguments through the exact registration; it never exposes those
Arguments as document children. Validator exceptions become a fixed invalid-arguments
refusal, without exception text. Confidential nodes remain unresolved on success.

### Render diagnostics

Every diagnostic is an error with only `Code`, canonical document `Path` and safe
`Source` (the projection's local identity, or empty for the template). The standard
code set is closed by the composer; validators cannot supply plugin-defined codes,
messages or parameters:

| Code | Meaning |
| --- | --- |
| `render.projection.invalid-path` | Not a canonical absolute non-root pointer; Path is empty and the rejected input is omitted. |
| `render.projection.template-collision` | Existing leaf or non-Object root/parent blocks insertion. |
| `render.projection.path-conflict` | Duplicate or ancestor/descendant projection; report each implicated path/source. |
| `render.formatter.unknown` | No definition for the exact formatter ID. |
| `render.formatter.unavailable` | Definition exists without an active implementation. |
| `render.formatter.arguments-invalid` | Typed validation refused or threw. |

Sort ordinally by Path, Code, Source and deduplicate identical triples. Return the
first 100 diagnostics and the complete distinct `ErrorCount`; `IsTruncated` marks
omitted errors. Any error refuses the whole result, regardless of truncation.
Invalid paths never echo raw input. Paths identify locations, never node values;
no formatter ID, SecretRef identity, Arguments, fragment/template values or exception
text is copied into diagnostic parameters. Reports do not alter import warnings.

### JSON profile and preview

`ConfigurationJson.Profile` is `haby.configuration-json/v1`. Ordinary serialization
returns a `ConfigurationJsonResult` with `Json = null` and `IsSuccess = false` if any
SecretReference or DeferredValue exists, including at the root or late in an Array.
It preflights confidentiality before creating output; it exposes no output sink.

The profile emits compact UTF-8 JSON without BOM, indentation or trailing newline,
with ordinal Object keys and Array order preserved. Strings/property names use
`JavaScriptEncoder.Default` escaping (including non-ASCII/HTML-sensitive characters,
JSON controls, quotes and backslashes). Exact numbers use ConfigurationNumber's
normalized signed coefficient, lowercase `e` and invariant decimal exponent:
remove leading/trailing coefficient zeroes, shift the exponent accordingly, and
spell every zero including negative zero as `0e0`. No double/decimal conversion is
used. These are document bytes, not the normalized manifest fingerprint codec.

`ConfigurationJson.Preview` returns a separate JSON envelope with `document` and
`redactedPaths`. A direct SecretReference or entire DeferredValue becomes the literal
marker `***REDACTED***`; deferred Arguments are never traversed. Paths are distinct,
canonical and ordinal, including empty for a confidential root. Literal marker text
has no redacted path. This API provides no delivery or reveal option.

### Existing consumers and compatibility

Manifest mapping remains ordinary JSON-only and cannot construct confidential
semantic nodes. Structural equality/diffing handles the deferred alternative as
one immutable value, without inspecting or emitting Arguments. The existing
normalized-manifest persistence codec deliberately rejects deferred nodes through
its unknown-kind refusal; this slice does not extend that codec or persist composed
documents. No schema/migration/catalog reset is required. The subsequent
[in-process Profile expansion slice](configuration-profiles-v1.md) now reuses the
structural checker for static contributions and unresolved Binding requests.
Formatter execution, application materialization, persistence and confidential
delivery remain future work.
