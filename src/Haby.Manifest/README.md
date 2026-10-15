# Manifest payload parser

`ManifestPayloadParser.Parse` is the shared, side-effect-free entry point for an
original UTF-8 import-envelope payload. Pass the bytes received from a caller;
do not deserialize the envelope in a transport adapter first.

The parser returns one of three typed outcomes:

- `Valid` contains the accepted application version and the source-generated
  `ApplicationManifest` DTO. It does not create a ManifestRevision.
- `InvalidRequest` contains `manifest-import.*` errors with paths relative to
  the import envelope.
- `InvalidManifest` contains an ordered `ManifestValidationReport` with
  `manifest.*` diagnostics relative to the manifest root.

The parser checks UTF-8 encoding, strict JSON syntax, duplicate object members,
the 64-level nesting limit and the closed envelope before exact `apiVersion` and
`kind` dispatch. The selected manifest runs through the embedded checked-in
v1alpha1 schema and then metadata-based DTO deserialization. Schema results are
mapped to Haby codes and safe parameters; library messages and input values are
not returned. Reports contain at most 100 distinct diagnostics and indicate
truncation when more exist.

Core semantic references, normalized mapping, extension contract validation and
import orchestration belong to later M2.1 slices.
