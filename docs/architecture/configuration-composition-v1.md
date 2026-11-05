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
