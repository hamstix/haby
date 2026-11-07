# ADR-040: PostgreSQL connection rendering and typed consumer options

- Status: Accepted
- Date: 2026-11-07
- Scope: P2 pure connection rendering, deferred formatting and exact planning metadata
- Extends: ADR-017/018/026/033/035/036/037/039

## Decision

### Separate renderer and formatter contracts

Register the exact renderer postgresql.npgsql-connection/v1alpha1 and formatter
postgresql.npgsql-connection-string/v1alpha1. Contract definitions and active
implementations are separate explicit registrations under ADR-018. Conflicting
definitions or ambiguous active implementations fail startup. These identities
are compatible semantic contracts, not assembly names or binary hashes.

The renderer is pure. Its input is the confirmed final application-login output:
contract postgresql.application-login-output/v1alpha1, key connection, plus typed
effective consumer settings. Existing output contains Host, Port, DatabaseName,
Schema, UserName and an exact Password SecretRef. Do not reconstruct these fields
from a current Provider alias/configuration, Resource name or caller override.
The shared selection boundary verifies exact Resource/generation, producer,
successful attempt and output identity before handing typed content to the adapter.
Planning checks declared output compatibility before the output exists; later
rendering requires actual confirmed evidence. Manual/uncertain/missing evidence
does not become a valid connection output.

Return one ConfigurationFragment whose value is a ConfigurationDeferredValue
promising a String. It carries the exact formatter contract, checked typed
connection arguments and Password ConfigurationSecretReference. It is not an
already materialized string or a special object supplied by user JSON.
No SecretStore, Provider/network I/O, whole-document mutation or Liquid execution
belongs in the renderer. No string concatenation or suffix option is supported.

### Closed PostgreSQL consumer options

The PostgreSQL adapter defines the complete allow-list. Similarity to driver
property names never permits arbitrary pass-through of driver keywords.

| JSON field | Type | Allowed values / unit | Renderer default |
| --- | --- | --- | --- |
| timeout | integer | 1-300 seconds, connection establishment | 15 |
| commandTimeout | integer | 1-3600 seconds, SQL command execution | 30 |
| sslMode | string enum | verify-full, require, disable | verify-full |

Unknown fields and explicit null refuse. An absent property inherits according
to renderer defaults < exact Profile rule.options < Component Binding.options.
Merge by top-level property replacement, never recursively. Missing options/{}
remain valid partial settings. Zero/infinite timeouts refuse. Mathematically
integral JSON values such as 200.0 and 2e2 are accepted as integer 200 without
double/decimal conversion. The old proposed names connectionTimeoutSeconds and
commandTimeoutSeconds are not aliases and are not part of this accepted contract.

Check individual partial settings and the final complete effective typed settings.
Application import uses the Resource/output binding-options metadata and exact
adapter schema/codec for supplied fields. Profile import remains portable;
planning resolves renderer availability, compatibility, Profile defaults and
complete effective options. Unknown or incompatible active render requests refuse.
Unused Profile rules still contribute nothing and require no output consumption.

Options cannot set host, port, database, username, password, SecretRef, schema,
privileges or Provider identity. Arbitrary connection-string parameters, suffixes,
Options, Passfile, password logging and custom Search Path are not admitted.
Use the application role's existing server-side search_path; do not reinterpret
Schema as permission to inject a new client search path. Pool tuning, application
names, client certificates and custom certificate paths remain later extensions.

### TLS semantics

verify-full requires TLS with certificate and hostname verification. require
requires encryption without server identity verification. disable explicitly
uses no TLS. verify-full is the Haby renderer default; isolated pilot Profiles
may explicitly choose disable. Do not inherit the driver's implicit TLS default.

A Profile's sslMode is a consumer default, not an enforced Environment security
floor. A later mandatory minimum-TLS policy is separate work. Defaults precedence
cannot weaken such a future explicitly enforced restriction.
Do not add Trust Server Certificate: the current Npgsql parameter is obsolete and
has no effect. Certificate trust is owned by the consuming application's runtime;
the renderer does not copy Haby-host certificate paths into another process.

### Profiles own document placement

The renderer produces a scalar value and does not know application section names.
For example, an exact Profile rule may request:

```json
{
  "slot": "main-database",
  "rendererRef": "postgresql.npgsql-connection/v1alpha1",
  "options": { "timeout": 15, "commandTimeout": 30, "sslMode": "verify-full" },
  "documentProjections": [
    { "documentRef": "settings", "path": "/PostgreSQL/DefaultConnection/ConnectionString" }
  ]
}
```

A separate Profile defines settings/appsettings.json with template {}. Existing
Object-only composition creates missing parent objects and refuses collisions.
Both api and worker may bind the same app-login with configurationSlot main-database.
worker inherits the Profile; api supplies Binding.options { "timeout": 200 }.
Their strings differ in Timeout, with identical confirmed database/user/SecretRef.
No additional role, credential slot, password rotation or ownership change occurs.

### Frozen planning and deferred execution

Extend ADR-039/#142's frozen structural inputs with resolved renderer/formatter
contracts, active compatible implementation identities, defaults and complete
effective options/provenance. Include them in strict snapshots/equality/fingerprints.
Decoding a saved snapshot must not apply current defaults again. Missing exact
capability refuses with a safe typed availability result; never choose another
implementation implicitly. No exact-binary compatibility guarantee is introduced.

The formatter builds the final string through NpgsqlConnectionStringBuilder and
explicit allowed typed properties, including Password supplied only at invocation.
It has no store, network, Provider or document access and cannot discover further
SecretRefs. The later authorized delivery boundary resolves only the previously
validated exact references, then invokes formatting and serializes transiently.
Missing/ambiguous registration or invalid/unavailable arguments fails the entire
operation; no empty-password fallback or partial delivery. String key order and
a trailing semicolon are not public semantics; parsed driver settings and actual
client behavior are the evidence.

The complete deferred value is confidential. Preview replaces the whole value
with ***REDACTED*** and records its path. Ordinary serialization refuses unresolved
secret/deferred nodes. Persist only typed arguments and exact references; never
persist materialized connection strings, plaintext/verifiers or material-derived
fingerprints. Npgsql requires managed strings; clear disposable buffers where
possible but do not promise erasure of every managed string copy.

### Provider neutrality and first delivery slice

Common code knows exact contracts, compatible output identity, schema/typed
validation seams, immutable fragments and deferred nodes. PostgreSQL field names,
option semantics, TLS mapping and Npgsql builder remain in the adapter. No
PostgreSQL-specific catalog tables/columns or common Provider-kind switch.
Use explicit generated DTO/JSON metadata and compatible extension registration.
A second synthetic renderer must traverse any new shared dispatch seam without
pretending that a second production Provider is implemented.

First implement closed options/contracts, explicit pure renderer/formatter and
planning validation/pinning. Prove formatting with explicitly supplied synthetic
secret material inside isolated tests, including a real PostgreSQL client check.
This does not authorize a production material resolver, reveal API, delivery
endpoint or persistence of materialized documents. Actual renderer invocation
with confirmed outputs is exercised through the shared seam; the operator-facing
document materialization/preview flow is the next slice.

If the current stored model changes, follow ADR-033: one current model with an
explicit fresh-catalog requirement where necessary, never silent reset/backfill
or compatibility layers solely for old alpha state. Current-build retry, restart,
restore, atomic assignment freshness and exact external-effect semantics remain.

## Required evidence

- Schema/typed-codec parity for partial and effective options; defaults/overrides,
  unknown/null/old-name refusal, range boundaries and exact integral spellings.
- api/worker share actual confirmed output/Password SecretRef but differ in
  timeout; output-derived identities cannot be overridden by consumer options.
- Exact renderer/output compatibility, explicit registration conflicts and safe
  unavailable implementations. Generic seam exercised by a second typed family.
- Pure renderer needs no secret material and returns a confidential deferred node.
  Whole-value preview redaction and ordinary serialization refusal remain intact.
- Formatter round-trip handles semicolon, equals, quotes, backslash and Unicode
  in synthetic passwords. Real disposable PostgreSQL accepts the generated string
  with correct credentials and refuses wrong credentials; TLS mapping is explicit.
- Plan snapshots retain resolved defaults/options/identities through restart and
  real restore. Changed current defaults never rewrite accepted inputs.
- Full solution/PostgreSQL/Native AOT gates and #140 measurement/isolation remain.
  Errors/logs/assertion artifacts must not expose strings/passwords/verifiers.

## References

- [Rendering foundation](0036-configuration-rendering-profiles-and-deferred-formatting.md)
- [Profile selection](0037-environment-configuration-profiles-and-scoped-selection.md)
- [Assignment and frozen-input boundary](0039-scoped-profile-assignment-administration.md)
- [PostgreSQL renderer contract](../architecture/postgresql-connection-rendering-v1alpha1.md)
- [Npgsql connection parameters](https://www.npgsql.org/doc/connection-string-parameters.html)
- [Npgsql security](https://www.npgsql.org/doc/security.html)
