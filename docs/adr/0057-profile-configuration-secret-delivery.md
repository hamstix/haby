# ADR-057: Profile configuration secrets and authorized delivery

- Status: Accepted
- Date: 2026-11-16
- Scope: Issue #209, immediately after #239
- Amends: ADR-038/039; extends ADR-028/029/040/041/042/043/050

## Context

Profiles already preserve explicit secret projection declarations, but execution
refuses them. Shared logging and telemetry configuration needs secret insertion
without inventing a Resource, Provider adapter or Component Binding. Declaration
support must not be mistaken for implemented delivery support.

## Decision

1. Add the Provider-neutral `Configuration` secret purpose for Environment-owned,
   named, immutable-version secrets. Reuse the existing secret catalog and backend.
2. Static Profile secret projections accept only this purpose. Administrator and
   generated ApplicationLogin secrets cannot be reused through this route. Existing
   secret purpose is immutable; generated login delivery retains Binding/renderers.
3. Extend named secret creation with `--purpose configuration`, retaining the
   existing administrative default, hidden input, exact stdin bytes, RequestId
   receipts and retry semantics. No secret value argument is introduced. Purpose
   participates in equivalent-create and conflict checks; Environment/name remains
   unique across purposes. A different purpose under the same name conflicts.
4. A Profile contains a secret name and explicit positive version. Import/read
   neither requires that secret to exist nor reads its material. Profiles remain
   reusable across Environments with different same-name secrets.
5. Planning resolves metadata in the exact Environment, checks purpose/version,
   and freezes the internal SecretRef. It reads no secret value. Delivery never
   re-resolves names or selects latest.
6. Trusted operator-admin assignment authorizes distribution to receiving
   Components, including future inheritors of Environment defaults. There is no
   per-secret operator ACL in this slice. Assignment changes do not mutate accepted
   Plans, generations or document revisions. Delivery authorization remains separate.
7. `string` inserts strict UTF-8 text exactly, without trimming or interpolation.
   `json` parses exactly one strict JSON value, retaining types and exact numbers;
   duplicate properties, comments, trailing content and malformed encoding/JSON
   fail. Data is never interpreted as commands, DeferredValue or nested SecretRef.
8. Amend ADR-038 object-only traversal **only for secretProjections**: traversal
   through arrays already present in ordinary `value` is allowed using canonical
   nonnegative, in-range indices. The final insertion must be a missing object
   property. No array creation, append (`-`), shifting, reordering, element
   replacement or occupied-value replacement is supported. Other projections retain
   their existing rules.
9. Missing object parents may be created. Occupied leaves, equal/overlapping paths
   and traversal through another secret insertion are rejected. The entire static
   contribution still owns its outer composition path; external projections cannot
   reach inside it.
10. Plans, snapshots, audits and ConfigurationDocumentRevision retain typed deferred
    exact references, formats and safe metadata, never secret material or parsed
    secret JSON. No fake Resource, Provider or Binding is required.
11. Preview reads no secret material, replaces each complete inserted node with
    `***REDACTED***`, and reports its final canonical path in redactedPaths. A JSON
    insertion is redacted as a whole node, not inspected for selective redaction.
12. Resolve only during authorized delivery through both existing scoped static
    delivery tokens and Kubernetes runtime authorization. Authenticate access to
    the exact document revision before reading secrets. A SecretRef is not an
    independent permission to read the secret catalog.
13. Missing/unavailable versions, wrong scope/purpose, invalid encoding/JSON,
    path errors and exceeded limits fail the whole document without partial
    output. Errors/logs never reproduce secret content or unsafe backend details.
14. Preserve ADR-042 bounds: each source secret at most 64 KiB; final JSON at most
    4 MiB UTF-8 and 64 container levels; complete RPC at most 8 MiB. Measure the
    actual expanded result. Do not introduce cross-request secret-material caches.
15. Extend existing reference retention and CleanUp checks for Plans, generations
    and documents to these exact versions. No automatic rotation or new secret
    revocation command. Initially replace a secret through a new name, new Profile
    revision and configuration-only generation; existing documents retain their
    exact references. Plaintext storage remains supported: projections prevent
    embedding material in Profiles/documents but do not encrypt the catalog or
    backups. Prefer encrypted storage where available; this slice does not add an
    encrypted backend.

## Example

An item of `staticContributions` can insert a password beneath an existing array:

```json
{
  "documentRef": "settings",
  "path": "/Serilog",
  "value": {
    "WriteTo": [{ "Name": "LokiHttp", "Args": {
      "lokiUrl": "https://logs.example.internal/insert/loki/api/v1/push",
      "lokiUsername": "logs-reader"
    }}],
    "MinimumLevel": { "Default": "Information" }
  },
  "secretProjections": [{
    "path": "/WriteTo/0/Args/lokiPassword",
    "format": "string",
    "secretRef": { "name": "logging-password", "version": 1 }
  }]
}
```

For an OpenTelemetry contribution, omit Headers from ordinary Otlp and project
`/Otlp/Headers`. Store the complete header string in the secret; this introduces
neither concatenation nor an OTEL-specific formatter. Never commit real endpoints
or credentials in examples.

## Delivery and compatibility

Issue #209 implements this end to end after #239. Until that implementation lands,
the existing bounded unsupported result remains the truthful runtime behavior.
ADR-039's initial execution deferral is superseded by this authorization, not by
an assertion that delivery is already implemented. Update current-alpha codecs
and explicitly document any fresh-catalog requirement under ADR-033; never reset
or backfill authority silently. Common persistence stays Provider-neutral.

Tests must prove no material reads before authorized delivery, exact references
across restart, whole-document failure, whole-node preview redaction, both delivery
authorization paths, retention, and array-boundary rejection. Source-generated
serialization and published Native AOT CLI behavior remain required.
