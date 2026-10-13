# Application manifest `haby.dev/v1alpha1` contract

- Status: Accepted
- Target milestone: M2.1
- Format: JSON
- Canonical schema ID:
  `https://haby.dev/schemas/application-manifest.v1alpha1.json`
- Related decisions: [ADR-009](../adr/0009-application-manifest-domain-model.md),
  [ADR-013](../adr/0013-m2-manifest-foundation-boundaries-and-identifiers.md),
  [ADR-014](../adr/0014-manifest-validation-diagnostics.md),
  [ADR-017](../adr/0017-structured-configuration-trees-and-document-projections.md),
  [ADR-018](../adr/0018-versioned-contract-identifiers-and-manifest-dependencies.md),
  [ADR-019](../adr/0019-typed-parameters-and-effective-value-resolution.md),
  [ADR-020](../adr/0020-versioned-application-manifest-json-contract.md)

## Purpose

This document is the normative field matrix for the first Application manifest
API version. It fixes required members, defaults, lexical constraints, closed
object shapes and cross-field rules used by the checked-in JSON Schema,
source-generated DTOs, normalized model and validation corpus.

All core object properties and discriminator values are case-sensitive. Unknown
core properties and explicit `null` values are invalid unless this document
states otherwise. Extension objects remain subject to their exact registered
contract schemas.

## Common lexical types

### Application ID

`metadata.id` uses the Application ID contract accepted by ADR-013:

```regex
^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$
```

Its maximum length is 128 ASCII characters.

### Manifest-local ID

Component, Resource, Resource export, Binding, Configuration document, Workload
and Parameter map keys use:

```regex
^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$
```

Their maximum length is 63 ASCII characters. The same value may appear in
different structural scopes; the owning path disambiguates it.

### Provider alias

`providerRef` is an Environment-local alias with a maximum length of 128 ASCII
characters and this syntax:

```regex
^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$
```

Dots and hyphens are naming conventions and have no resolution semantics.

### Versioned contract ID

`ResourceTypeId`, `WorkloadTypeId`, `OutputContractId` and
`ConfigurationRendererId` are distinct typed values with the shared lexical
form:

```regex
^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*/v[1-9][0-9]*(?:(?:alpha|beta)[1-9][0-9]*)?$
```

Examples include `postgresql.database/v1`,
`postgresql.database/v1alpha1` and `postgresql.connection/v2beta3`. Unversioned
IDs, `latest` and ranges are invalid.

### Display names and labels

An optional `displayName` is a non-empty string with at most 256 Unicode scalar
values. Absence is preserved; a user interface may display the object's ID as a
fallback without materializing that fallback into the normalized declaration.

Application and Component `labels` are optional maps that default to empty and
contain at most 64 entries. A label key uses the Application ID syntax and has a
maximum length of 128 ASCII characters. A label value is lowercase ASCII, has a
maximum length of 63 characters and matches:

```regex
^(?:[a-z0-9](?:[a-z0-9._-]{0,62})?)?$
```

The empty value is valid for a presence label. Keys and values compare using
ordinal case-sensitive equality. M2.1 does not add labels to Resource, Binding
or Workload declarations.

## Root and metadata

The root requires `apiVersion`, `kind`, `metadata` and `spec`. `$schema` is
optional and, when present, must equal the canonical schema ID above.

```json
{
  "$schema": "https://haby.dev/schemas/application-manifest.v1alpha1.json",
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "Application",
  "metadata": {
    "id": "processor-service",
    "displayName": "Processor",
    "labels": {
      "team": "platform"
    }
  },
  "spec": {}
}
```

`metadata.id` is required. `metadata.displayName` is optional and
`metadata.labels` defaults to an empty map.

`spec.parameters`, `spec.resources`, `spec.exports` and `spec.components` are
optional maps that default to empty. `spec: {}` is valid. An Application with no
Components is valid so an infrastructure-only Application can own Resources and
optionally expose them to Components of other Applications.

## Resources

A Resource requires `type` and `providerRef`:

```json
{
  "type": "postgresql.database/v1alpha1",
  "providerRef": "postgresql.primary",
  "properties": {},
  "lifecycle": {
    "deletionPolicy": "retain"
  }
}
```

`properties` is an extension JSON object and defaults to empty. Arrays, scalars
and `null` are invalid. `lifecycle` is optional and defaults to
`{ "deletionPolicy": "retain" }`. The only deletion-policy values are:

```text
retain
orphan
delete
```

The policy has no effect while the Resource remains declared. Its operational
semantics after explicit removal are defined by ADR-009.

## Resource exports

A Resource export requires `resourceRef` and `contract`:

```json
{
  "resourceRef": "main-database",
  "contract": "postgresql.connection/v1alpha1",
  "accessProfile": "read-only"
}
```

`accessProfile` is optional and, when present, uses the manifest-local ID
syntax. Its absence means that the owner declares no additional
provider-defined access profile; it does not imply read-write access.

## Components

A Component may contain only:

```text
displayName
labels
bindings
documents
workloads
```

All members are optional. `bindings`, `documents` and `workloads` default to
empty maps. A Component with no nested declarations is valid. M2.1 does not add
a Component `type`.

## Bindings and targets

A Binding requires `target`. `options` is an extension JSON object that defaults
to empty. `documentProjections` defaults to an empty list. A Binding without a
Document projection is valid because it may establish Resource access without
placing Resource outputs into a Configuration document.

A local target has exactly this closed shape:

```json
{
  "kind": "local-resource",
  "resourceRef": "main-database"
}
```

An external target has exactly this closed shape:

```json
{
  "kind": "application-export",
  "applicationId": "shared-storage-service",
  "exportRef": "reporting-database",
  "contract": "postgresql.connection/v1alpha1"
}
```

Every shown member is required. The discriminator selects one explicit
source-generated DTO; members from another alternative are invalid.

## Document projections

A Document projection requires all three members:

```json
{
  "documentRef": "settings",
  "path": "/PostgreSql",
  "rendererRef": "postgresql.npgsql-connection/v1alpha1"
}
```

`path` is a non-empty absolute RFC 6901 JSON Pointer in canonical
decoded-and-reencoded form. It cannot target the root or traverse an Array.
Array indexes and append operations are not supported in M2.1; numeric and `-`
segments remain ordinary Object member names when their parent is an Object.

`documentProjections` has set semantics. Exact duplicates are invalid. The
normalized declaration orders entries by `documentRef`, canonical `path` and
`rendererRef` using ordinal comparison, so source order does not affect equality
or execution. Projection path collisions are evaluated across every Binding of
the containing Component.

## Configuration documents

`format` is the required discriminator with exactly three values:

```text
json
yaml
text
```

Every Configuration document requires `fileName`, `format` and `template`.
`fileName` is an output artifact name, not a delivery path. It has at most 255
ASCII characters and matches:

```regex
^[A-Za-z0-9][A-Za-z0-9._-]{0,254}$
```

It is one path segment: `/`, `\`, `.` and `..` are invalid names. Two Documents
inside one Component cannot have the same `fileName`; different Components may
use the same value.

`json` and `yaml` documents contain a required structured template represented
with JSON syntax. Its root may be Object, Array, String, Boolean, exact Number or
Null. If any Binding projects into the document, the template root must be an
Object; Haby never replaces an Array or scalar with an Object implicitly.

A `text` document contains a required JSON string template. An empty string is
valid. Text documents cannot be projection targets and do not support structured
composition or SecretRef interpolation in M2.1.

## Workloads

A Workload requires `type` and `providerRef`:

```json
{
  "type": "kubernetes.workload/v1alpha1",
  "providerRef": "kubernetes.primary",
  "profile": "worker",
  "properties": {}
}
```

`profile` is optional and uses the manifest-local ID syntax. `properties` is an
extension JSON object that defaults to empty. A Workload has no `componentRef`
because its containing Component defines that relationship structurally.

## Parameters

Parameter definitions use the closed `string`, `boolean`, `integer` and
`number` alternatives from ADR-019. `type` is required. `description` is
optional and has at most 2048 Unicode scalar values. `default` and
`defaultExpression` are mutually exclusive, and explicit `null` is invalid.

`allowedValues` has set semantics. It must be non-empty when present, contains
no values equal under the declared parameter kind and is normalized by exact
typed value. Source order does not affect normalized equality. Every allowed
value must also satisfy the definition's other constraints.

## Semantic validation

After core schema validation, semantic validation verifies at least that:

- every local reference resolves and has the expected declaration kind;
- every Resource export references a Resource in the same Application;
- every local Binding target references a Resource in the same Application;
- every projection references a Document in the same Component;
- direct references to another Application's internal Resource are impossible;
- no text Document is a projection target;
- file names are unique within one Component;
- canonical projection paths do not collide;
- type-specific extension objects satisfy their exact registered schemas.

A Resource with no Binding or Resource export remains valid and produces the
`manifest.resource.unused` warning. Component removal or loss of the last
consumer never removes a still-declared Resource.

## Ordering and limits

JSON object and map member order is insignificant. Structured template Array
order remains significant. `documentProjections` and parameter `allowedValues`
are the only M2.1 core arrays with explicit set semantics.

The domain contract does not impose arbitrary limits on the number of
Components, Resources, exports, Bindings, Documents, Workloads or Parameters.
Resource protection comes from configurable transport request-size limits, a
JSON nesting limit of 64, a validation-report limit of 100 diagnostics, lexical
field limits and cancellation-aware validation.
