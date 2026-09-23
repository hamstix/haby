# ADR-015: Normalized manifest equality and immutable version imports

- Status: Accepted
- Date: 2026-09-23

## Context

ADR-010 requires one `ApplicationId + ApplicationVersion` pair to identify
exactly one immutable declaration. An equivalent repeated import must return the
existing ManifestRevision, while different content under the same pair must
produce a stable conflict. Exact source bytes, JSON formatting and DTO
serialization cannot define that equivalence because Haby imports a structured
declaration rather than a physical file.

The legacy registry exposed a `--replace` option that could overwrite content
under the same version and force provisioning. That flag combined declaration
replacement, JSON merge recovery and provider re-execution into one ambiguous
operation. Haby separates immutable declarations, effective desired state and
reconciliation, so retaining that behavior would weaken all three models.

## Decision

### Equality compares validated normalized declarations

Haby maps a validated manifest DTO to an immutable
`NormalizedApplicationManifest`. Equality is defined explicitly over that model;
it does not use original bytes, reserialized DTO bytes or default CLR record
equality.

The following structural rules apply:

| Value | Equality rule |
| --- | --- |
| JSON object members | Order is ignored; decoded property names are compared exactly |
| Arrays | Order is preserved and significant unless a field contract explicitly declares set semantics |
| Whitespace and indentation | Ignored by JSON parsing |
| JSON string escapes | Compared after decoding, so `"a"` and `"\u0061"` are equal |
| Strings and raw text | Compared ordinal; case, whitespace and Unicode composition are not implicitly normalized |
| Maps and dictionaries | Compared by exact key and normalized value, independent of source order |
| `null` and an absent member | Different unless the field contract explicitly defines equivalence |
| Duplicate object members | Rejected before normalization |
| Unknown manifest members | Rejected rather than silently discarded |

Arrays are never globally sorted or deduplicated. A collection that behaves as a
set must declare that rule in its versioned contract. Liquid templates and raw
text documents remain exact strings because their whitespace and ordering may be
meaningful.

### JSON numbers use exact mathematical normalization

Mathematically equal JSON numbers are equal regardless of lexical form. For
example, `1`, `1.0`, `1e0` and `1.00e+0` normalize to the same value. `0`, `-0`
and `0.0` normalize to the same zero.

Normalization represents a JSON number exactly as sign, arbitrary-precision
coefficient and decimal exponent. It never passes through `double` and therefore
does not lose large integers or decimal precision.

### Core defaults are materialized; extension defaults are not

The versioned Haby manifest contract materializes its defined defaults in the
normalized model. Omitting a core field and explicitly supplying its contract
default therefore produce equal declarations.

In M2.1, type-specific `Resource.properties` and `Workload.properties` are
validated and structurally normalized, but Haby does not apply a JSON Schema
`default` annotation. An absent extension property remains different from an
explicit property. Type-specific semantic normalization is deferred until a
real plugin use case justifies a separate versioned capability.

### Declaration content and import metadata remain separate

Normalized equality includes all meaningful declaration content, including:

- `apiVersion`, `kind` and `metadata.id`;
- display metadata and labels;
- parameters;
- Resources and Resource exports;
- Components, Configuration documents, Bindings, projections and Workloads;
- normalized structured templates and type-specific `properties`.

It excludes:

- `$schema`, which is an authoring and editor hint;
- `applicationVersion`, which is a separate import-envelope value;
- import time, actor and producer-private provenance;
- validation diagnostics and fingerprints;
- Environment-specific Provider resolution, overrides and observed state.

Changing display metadata or labels does not replace the Application identity,
but it does change the immutable declaration and therefore requires a new
Application version.

### Normalization profiles are versioned

Normalization semantics are selected by manifest `apiVersion`. A
ManifestRevision records the normalization profile used for its accepted
declaration. Haby retains compatible profiles for supported manifest API
versions; changing equality semantics requires a new manifest API version or an
explicit migration that preserves comparison with existing revisions.

An ordinary Haby upgrade must not silently change whether an existing
declaration is equal to a repeated import.

### Fingerprints are internal collision-safe accelerators

Haby may serialize the normalized declaration into a versioned canonical
representation and compute `SHA-256` over those bytes. The resulting
`ManifestFingerprint` is server-generated and records the canonicalization or
normalization profile used to produce it.

The fingerprint is not:

- supplied by an import client;
- a digest of the physical `haby.json` file;
- a portable release identity;
- a substitute for `ApplicationId + ApplicationVersion`;
- authoritative proof of equality by itself.

For the same fingerprint algorithm and normalization profile, a differing
fingerprint proves that declarations differ. Matching fingerprints are followed
by comparison of the canonical normalized representation or the normalized
domain model. This final comparison remains authoritative and avoids making
hash-collision assumptions part of domain correctness.

### Repeated imports preserve the first accepted revision

When `ApplicationId + ApplicationVersion` does not exist, Haby validates,
normalizes and stores a new ManifestRevision and its successful-import warnings.

When the pair already exists, Haby parses and normalizes the incoming declaration
using the profile compatible with the stored revision:

- an equal declaration returns the existing ManifestRevision;
- a different declaration returns `application-version-content-conflict` and
  creates no revision;
- a new Application version creates a new ManifestRevision even if its
  declaration equals an earlier version.

An equal repeated import does not replace the first accepted source declaration,
import time, actor or historical warnings. It does not implicitly revalidate the
stored revision under newer rules. An explicit audit event for a repeated import
may be added later without mutating the revision.

### Version conflicts disclose paths, not values

`application-version-content-conflict` is an application error, not a manifest
validation diagnostic. Its public application result may contain:

- `ApplicationId` and `ApplicationVersion`;
- the existing local revision reference;
- deterministic, sorted changed JSON Pointer paths;
- a flag indicating that the changed-path list was truncated.

M2.1 limits the list to 100 paths. The result never includes old or new values,
secret material or complete configuration fragments.

### Existing Application versions cannot be replaced

Haby does not support replacing or overwriting an existing
`ApplicationId + ApplicationVersion`. Import, CLI and future public APIs do not
provide `replace`, `overwrite`, `force-import` or equivalent escape hatches. A
producer changes a declaration by publishing a new Application version.

Re-executing provider work is modeled as retry, resume or reconciliation against
the existing desired revision, never as manifest replacement:

- an already converged deployment is a successful no-op;
- interrupted or failed idempotent work is resumed or retried;
- external drift is reconciled toward the same desired state;
- credential rotation and destructive Resource replacement are explicit scoped
  operations rather than side effects of reimport.

M2.1 defines this separation but does not execute providers or add a forced
reconciliation command. Later reconciliation operations must be scoped, durable
and audited without modifying the ManifestRevision or regenerating state merely
because an administrator requested another attempt.

## Consequences

- Formatting, object order, escaping and explicit core defaults do not create
  false version conflicts.
- Ordered configuration remains ordered and meaningful text remains exact.
- Equality is deterministic without floating-point precision loss.
- Plugin-owned defaults cannot silently alter declaration identity in M2.1.
- The first accepted declaration and warnings remain immutable and explainable.
- A conflict can identify changed locations without disclosing their values.
- Provider recovery no longer weakens Application-version immutability.
- Different Application versions can intentionally create distinct revisions
  with equal normalized declarations.

## Rejected alternatives

### Compare exact source bytes or reserialized DTOs

Whitespace, member order, escaping and serializer behavior would create false
conflicts unrelated to declaration semantics.

### Use SHA-256 equality without a final comparison

Fingerprints are useful indexes and fast rejection checks, but domain correctness
must not depend on treating a hash as an infallible identity.

### Sort every array during normalization

Many arrays express precedence or execution order. Sorting without a field-level
contract would change their meaning.

### Apply plugin JSON Schema defaults automatically

JSON Schema defaults are annotations and plugin normalization semantics have not
yet been designed. Treating them as mutations would create implicit behavior and
versioning obligations.

### Replace the declaration and provision again

This combines source mutation with operational recovery, destroys immutable
release identity and can accidentally regenerate credentials or external names.
Retry, resume and reconciliation are separate operations over existing desired
state.

## Follow-up work

- implement the versioned normalized model, exact JSON number representation and
  comparer with focused equivalence tests;
- implement canonical representation and internal fingerprinting as an
  optimization after equality tests are authoritative;
- define the typed version-content conflict application result;
- specify retry, resume and reconciliation operations in ADR-005 without adding
  manifest-replacement semantics.
