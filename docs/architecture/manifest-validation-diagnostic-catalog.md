# M2.1 manifest validation diagnostic catalog

- Status: Accepted
- Target milestone: M2.1
- Related decision: [ADR-014](../adr/0014-manifest-validation-diagnostics.md)
- Manifest contract:
  [`haby.dev/v1alpha1`](application-manifest-v1alpha1.md)

## Purpose

This document is the normative initial catalog of manifest validation codes,
severities, parameters and path rules. It keeps validation independent from the
selected JSON Schema library, `System.Text.Json`, Liquid, plugins and future
REST or gRPC adapters.

`ManifestDiagnostic.Code` is a string-backed value rather than a transport or
CLR enum. Haby exposes known codes as constants. M2.1 validators may emit only
codes registered in this catalog. Adding a code is compatible; removing a code,
changing its meaning or changing its required parameter names requires public
contract consideration.

Each code has one catalog-defined severity. Validators cannot select severity
independently. `manifest.resource.unused` is the only M2.1 warning; every other
code in this catalog is an error.

## Code selection

One violation produces one most-specific diagnostic code. Generic schema codes
are used only when no domain-specific code exists. In particular:

- an unknown Parameter `type` produces
  `manifest.parameter.type-invalid`;
- an unknown Binding `kind` or Configuration document `format` produces
  `manifest.schema.invalid-discriminator`;
- a syntactically valid but unregistered Resource or Workload type produces
  `manifest.type.unknown`;
- a missing `documentRef` produces `manifest.reference.not-found`;
- `manifest.document.projection-target-invalid` applies only to an existing
  text Document or an existing structured Document with a non-Object root.

The same defect is never repeated as both a generic and a domain-specific
diagnostic.

## Syntax codes

| Code | Required parameters | Meaning |
| --- | --- | --- |
| `manifest.syntax.invalid-encoding` | `expectedEncoding` | Input is not UTF-8 without a byte-order mark |
| `manifest.syntax.invalid-json` | none | JSON syntax is invalid |
| `manifest.syntax.duplicate-property` | `property` | One Object contains the same member name more than once |
| `manifest.syntax.max-depth-exceeded` | `maxDepth` | JSON nesting exceeds the M2.1 limit of 64 |

`manifest.syntax.invalid-json` never exposes a source fragment or parser
exception text. A duplicate-property diagnostic points to the duplicate member
and uses the location of its second occurrence.

## Header and core schema codes

| Code | Required parameters | Meaning |
| --- | --- | --- |
| `manifest.schema.unsupported-api-version` | `apiVersion` | The manifest API adapter is not supported |
| `manifest.schema.unsupported-kind` | `kind` | The root document kind is not supported |
| `manifest.schema.schema-uri-mismatch` | `expectedSchema` | `$schema` does not match the selected `apiVersion` |
| `manifest.schema.required` | `property` | A required member is absent |
| `manifest.schema.unknown-property` | `property` | A core Object contains an unknown member |
| `manifest.schema.type-mismatch` | `expectedType` | A JSON token has the wrong core type |
| `manifest.schema.invalid-discriminator` | `property` | A closed union has an unknown discriminator value |
| `manifest.schema.constraint-violation` | `constraint` | A structural JSON Schema constraint is violated |

The schema adapter maps keywords such as `minLength`, `maxLength`, `minimum`,
`maximum`, `pattern`, `minItems`, `maxItems` and `uniqueItems` to
`manifest.schema.constraint-violation`. Haby does not publish one diagnostic
code per JSON Schema keyword.

An invalid closed union produces one `invalid-discriminator` diagnostic. Errors
from every unmatched `oneOf` branch are suppressed. After a known discriminator
is selected, only its branch is evaluated.

## Identifier codes

| Code | Required parameters | Meaning |
| --- | --- | --- |
| `manifest.id.invalid` | `idKind` | An ID violates its syntax or length |
| `manifest.id.duplicate` | `idKind` | A logical identity is duplicated outside ordinary JSON-map uniqueness |

The initial `idKind` values are:

```text
application
component
resource
resource-export
binding
document
workload
parameter
provider-alias
contract
access-profile
```

A duplicate JSON map member is a syntax error and therefore uses
`manifest.syntax.duplicate-property`, not `manifest.id.duplicate`.

## Reference and contract codes

| Code | Required parameters | Meaning |
| --- | --- | --- |
| `manifest.reference.not-found` | `targetKind`, `reference` | A local reference cannot be resolved |
| `manifest.reference.kind-mismatch` | `expectedKind`, `actualKind` | A reference resolves to the wrong declaration kind |
| `manifest.reference.invalid` | `referenceKind` | A reference form is not permitted at this location |
| `manifest.type.unknown` | `contractKind`, `contractId` | No exact contract definition is registered |
| `manifest.properties.schema-violation` | `contractId`, `constraint` | An extension Object violates its exact registered schema |

The initial `contractKind` values are:

```text
resource
workload
output
renderer
binding-options
```

Plugin or schema-library codes and messages never pass through this boundary.
Every extension-schema failure maps to
`manifest.properties.schema-violation` at the precise manifest path.

## Parameter codes

| Code | Required parameters | Meaning |
| --- | --- | --- |
| `manifest.parameter.type-invalid` | `parameterType` | The Parameter discriminator is unknown |
| `manifest.parameter.default-conflict` | none | `default` and `defaultExpression` are both present |
| `manifest.parameter.default-type-mismatch` | `expectedType` | A literal default has the wrong JSON scalar type |
| `manifest.parameter.constraint-invalid` | `constraint` | Constraints are malformed or contradictory |
| `manifest.parameter.allowed-value-duplicate` | none | `allowedValues` contains an equal typed value more than once |
| `manifest.parameter.default-expression-invalid` | `reason` | A default expression is invalid or uses a forbidden capability |

The allowed default-expression reasons are:

```text
syntax
unknown-symbol
forbidden-operation
```

Diagnostics never include the expression source.

## Configuration document codes

| Code | Required parameters | Meaning |
| --- | --- | --- |
| `manifest.document.file-name-conflict` | `fileName` | Two Documents in one Component use the same artifact name |
| `manifest.document.template-invalid` | `reason` | A structured or text template violates its form contract |
| `manifest.document.projection-target-invalid` | `reason` | An existing Document cannot accept a projection |
| `manifest.document.projection-path-invalid` | `reason` | A projection JSON Pointer violates the M2.1 subset |
| `manifest.document.projection-duplicate` | none | An equal projection appears more than once |
| `manifest.document.projection-collision` | `reason` | A template or another projection occupies an incompatible path |

The initial template reasons are:

```text
liquid-syntax
templated-key
```

The projection-target reasons are:

```text
text-document
non-object-root
```

The projection-path reasons are:

```text
root
invalid-pointer
array-traversal
```

The projection-collision reasons are:

```text
same-path
ancestor-descendant
existing-leaf
non-object-parent
```

A missing target Document is a reference failure and never a
`projection-target-invalid` diagnostic.

## Resource and Workload codes

| Code | Severity | Required parameters | Meaning |
| --- | --- | --- | --- |
| `manifest.resource.unused` | Warning | none | A Resource has no Binding or Resource export |
| `manifest.resource.export-invalid` | Error | none | A Resource export violates its owner or Resource relationship |
| `manifest.workload.invalid` | Error | none | A Workload violates a core invariant not covered by schema or type codes |

The last two codes are deliberate fallbacks for domain invariants. A more
specific existing code always wins, and new narrow codes are added only after a
real invariant requires them.

## Paths and related paths

`Path` is an RFC 6901 JSON Pointer relative to the root of `haby.json`; it never
contains the import-envelope `/manifest` prefix. Examples are:

```text
/metadata/id
/spec/resources/main-database/type
/spec/components/api/bindings/database/target/resourceRef
/spec/components/api/documents/settings/fileName
```

Path rules are:

- a missing required member points to the path that member would have occupied;
- an unknown property points to that property;
- an invalid map key points to its map member;
- a discriminator error points to its `kind`, `format` or `type` field;
- a projection conflict points to the primary projection's `path` field;
- every segment uses RFC 6901 escaping.

`RelatedPaths` is used only when several declarations participate, including
file-name conflicts, duplicate projections, overlapping projection paths and
related reference conflicts. The ordinally first manifest path is primary; the
remaining paths are sorted ordinally and stored in `RelatedPaths`.

## Source locations

`Location` is optional. Syntax validation uses it where reliable:

- invalid JSON uses the parser location;
- duplicate property uses the second declaration's location;
- maximum depth uses the location where the limit was exceeded.

Schema and semantic diagnostics need not contain a source location when the
validator cannot map a normalized node reliably back to exact source
coordinates. Absence of a location does not weaken the diagnostic contract.

## Safe parameters and messages

Every code has the fixed parameter names listed in this catalog. Parameter
values are bounded to 256 characters. Only fixed schema keywords, expected
types, bounded safe identities and contract IDs may be included.

> Diagnostic parameters describe the violated contract, expected shape and
> involved safe identities. They never reproduce the invalid configuration
> value itself.

Credentials, arbitrary JSON, template content, property values, external
library messages and exception text are forbidden in parameters and messages.
An invalid raw value is not echoed. A reference value is included only after it
has passed its own bounded identifier validation.

`Message` is non-empty English text generated by Haby. It may improve without a
contract-version change and is not used for machine decisions or exact test
assertions.

## Library adapter requirements

A JSON Schema adapter must:

- translate library results into this catalog;
- normalize all paths;
- suppress branch-error noise;
- never expose library codes, messages or exception types;
- produce the same semantic report after replacement with another validator.

Liquid and extension-schema adapters follow the same boundary.

## Deduplication, ordering and truncation

Two diagnostics are identical when these values match:

```text
Code
Severity
Path
Parameters
RelatedPaths
```

`Message` and `Location` do not participate in diagnostic identity. Exact
duplicates are removed before ordering and truncation.

The final order is ordinal by:

```text
Path
Code
Parameters ordered by key and value
RelatedPaths
```

Severity does not participate because it is fixed by code.

M2.1 returns at most 100 diagnostics. Validation follows dependency order and
deterministic traversal within each stage. After deduplication and ordering, the
first 100 diagnostics are retained and `Truncated` is set. Later validation
stages do not run once the limit is exhausted.

## Stage gating

Validation runs in this order:

```text
syntax and duplicate members
  -> core schema
  -> source-generated DTO deserialization
  -> core semantic references
  -> registered extension schemas
```

A stage runs only when the previous stage produced a sufficiently valid model.
Invalid JSON does not create schema diagnostics; an unknown discriminator does
not create errors from every branch; and a missing Resource does not create
extension-schema errors for that Resource.

## Test corpus

At least one focused fixture covers every code. The shared validation corpus
also verifies:

- exact paths and related paths;
- most-specific-code selection;
- deterministic ordering and deduplication;
- the 100-diagnostic truncation boundary;
- `oneOf` branch suppression;
- redaction of invalid and sensitive values;
- equivalent reports from local validation and import;
- persistence of successful-import warnings with the immutable
  ManifestRevision.

Tests assert code, severity, path, parameters and related paths. They do not
assert exact message text beyond checking that it is non-empty and safe.

## Application errors outside the catalog

The following are application or transport errors rather than manifest
diagnostics:

```text
manifest-import.invalid-encoding
manifest-import.invalid-json
manifest-import.duplicate-property
manifest-import.required
manifest-import.unknown-property
manifest-import.type-mismatch
manifest-import.invalid-application-version
manifest-import.request-too-large
authentication failure
authorization failure
application-version-content-conflict
deployment-change-set-stale
deployment-plan-stale
optimistic concurrency conflict
provider alias resolution failure
external export resolution failure
```

Their typed result contracts are defined by the owning application use case,
beginning with manifest validation and import in
[ADR-021](../adr/0021-application-manifest-validation-and-import-use-cases.md).
