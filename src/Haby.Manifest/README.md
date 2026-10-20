# Manifest payload parser

`ManifestPayloadParser.Parse` is the shared, side-effect-free entry point for an
original UTF-8 import-envelope payload. Pass the bytes received from a caller;
do not deserialize the envelope in a transport adapter first.

The parser returns one of three typed outcomes:

- `Valid` contains the accepted application version, the source-generated
  `ApplicationManifest` DTO and a valid report that may contain warnings. It
  does not create a ManifestRevision.
- `InvalidRequest` contains `manifest-import.*` errors with paths relative to
  the import envelope.
- `InvalidManifest` contains an ordered `ManifestValidationReport` with
  `manifest.*` diagnostics relative to the manifest root.

The parser checks UTF-8 encoding, strict JSON syntax, duplicate object members,
the 64-level nesting limit and the closed envelope before exact `apiVersion` and
`kind` dispatch. The selected manifest runs through the embedded checked-in
v1alpha1 schema, metadata-based DTO deserialization and local semantic checks.
The semantic checks resolve Resource exports, local Binding targets and Document
projections, reject text projection targets and duplicate Document file names,
and warn about unused Resources. An external application export target is not
resolved here. Schema results are mapped to Haby codes and safe parameters;
library messages and input values are not returned. Reports contain at most 100
distinct diagnostics and indicate truncation when more exist.

`ConfigurationValueMapper.FromValidatedJson` copies a schema- and semantics-
validated `JsonElement` for a structured template or extension object into the
immutable Haby.Domain tree. It preserves exact JSON numbers and does not retain
the input `JsonDocument`. It rejects duplicate object members. JSON objects,
including ones with a `$secretRef` key, remain ordinary Object nodes; no JSON
shape creates a typed `ConfigurationSecretReference`. Trusted domain operations
can construct that node from an exact-version `SecretRef`.

The internal `NormalizedApplicationManifestMapper` copies a validated
v1alpha1 DTO into the immutable typed domain graph (normalized model version 1).
Closed declarations, references and discriminators have domain types; only
structured templates, Resource/Workload properties and Binding options use
`ConfigurationValue`. The mapper materializes core defaults, sorts the two
set-like declaration fields and preserves exact numbers and raw text.

The mapper is not called by the parser. The parser's current `Valid` result
does not prove that registered extension contracts were checked. A future
Validate/Import pipeline must complete that validation before calling the
mapper or accepting a ManifestRevision. Fingerprints, persistence and import
orchestration remain separate work.
