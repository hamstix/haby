# ADR-019: Typed parameters and effective value resolution

- Status: Accepted
- Date: 2026-10-13

## Context

Application manifests need portable typed inputs for values such as query
prefixes. Existing templates also commonly derive defaults from an Environment,
for example the legacy equivalent of `%env%_`. Haby must support that migration
without treating every string as Liquid, mixing plugin-schema defaults with
Application parameters or allowing operator values to be overwritten by a new
manifest revision.

M2.1 requires enough parameter semantics to parse and validate `haby.json`,
resolve deterministic values during planning and render Configuration documents.
It does not require PostgreSQL persistence, assignment-management APIs or UI.

## Decision

### Parameter definitions belong to the Application

Parameter definitions are declared only in `spec.parameters`. Their map keys are
stable manifest-local kebab-case `ParameterId` values. Definitions are visible
to every Component in the Application; M2.1 does not introduce separate
Component-owned definitions.

Parameter definitions and operator-owned assignments are separate domain
concepts. A manifest never stores the current operator value.

### M2.1 parameter values are scalar and non-null

M2.1 supports four parameter kinds:

```text
string
boolean
integer
number
```

`integer` uses an arbitrary-precision mathematical integer. `number` uses the
exact coefficient-and-decimal-exponent representation accepted by ADR-015 and
never passes through `double`.

For the `haby.dev/v1alpha1` wire DTO, `number`-kind `minimum`, `maximum` and
`allowedValues` entries use an exact JSON-number value type owned by
`Haby.Manifest`, with a metadata-compatible `System.Text.Json` converter. The
wire type preserves arbitrary precision and converts to the domain's exact
number representation during normalization; it is not itself a domain model.
`double` and bounded `decimal` are not valid intermediate representations.
The separate typed literal `default` remains a `JsonElement` boundary.

Arrays, Objects, Null and SecretReference are not parameter kinds in M2.1. A
later version may add a type only with explicit validation, normalization,
Liquid-conversion and compatibility rules.

### Definitions are an explicit source-generated union

`type` is the discriminator for a closed set of DTOs:

```text
ParameterDefinition
|- StringParameterDefinition
|- BooleanParameterDefinition
|- IntegerParameterDefinition
`- NumberParameterDefinition
```

The contracts use metadata-based `System.Text.Json` source generation. They do
not use `object`, reflection-based discovery or converters that infer a type
from the shape of `default`.

### Literal and expression defaults are different fields

A definition may declare either a literal default:

```json
{
  "type": "string",
  "default": "local_"
}
```

or a Liquid default expression:

```json
{
  "type": "string",
  "defaultExpression": "{{ environment.key }}_"
}
```

`default` and `defaultExpression` are mutually exclusive. A string in `default`
is always literal, even when it contains `{{` and `}}`. This avoids implicit
template interpretation and makes migration from legacy `%env%` placeholders
explicit.

A definition with neither field is required. M2.1 does not add a redundant
`required` flag. Explicit JSON `null` is not equivalent to absence and is invalid
for every M2.1 parameter kind.

### Literal defaults are validated during import

Import requires a literal default to have the exact declared JSON scalar kind
and satisfy every constraint. Haby performs no string-to-number,
number-to-Boolean or similar coercion.

For example, `"3"` is not integer `3`, numeric `1` is not Boolean `true`, and
`"yes"` is not a Boolean value. Invalid defaults prevent creation of a
ManifestRevision.

### Environment has a stable symbolic key

Environment identity separates:

```text
EnvironmentRecordId  internal aggregate-specific UUIDv7 identity
EnvironmentKey       stable installation-local symbolic ID
DisplayName          mutable operator-facing metadata
```

`EnvironmentKey` is a lowercase kebab-case value such as `production` or
`staging`. Ordinary display-name changes do not change it. The Liquid planning
context exposes this value as `environment.key`; it never substitutes the
mutable display name or internal UUID.

### Default expressions are evaluated during planning

Import parses and validates Liquid syntax and the allowed symbol surface but
does not evaluate `defaultExpression`, because no Environment is selected.

Planning evaluates the expression against its exact context, converts the
result to the declared scalar kind and validates the constraints. The resolved
typed value and its context-dependent origin become part of the immutable
effective desired-state snapshot.

An expression failure or result-type mismatch is a planning error rather than a
retroactive ManifestRevision diagnostic.

### The default-expression context is closed and deterministic

M2.1 exposes only:

```text
environment.key
application.id
```

Default expressions cannot read other parameters, Resource outputs, SecretRefs,
process environment variables, arbitrary services, current time, randomness or
other non-deterministic state. Excluding parameter references prevents a hidden
dependency graph and default-expression cycles.

### Constraints are typed and deliberately small

M2.1 supports:

```text
string:   minLength, maxLength, allowedValues
integer:  minimum, maximum, allowedValues
number:   minimum, maximum, allowedValues
boolean:  no additional constraints
```

Bounds are inclusive. `allowedValues` entries have the exact declared parameter
kind and use ADR-015 number equality where applicable. Contradictory constraints
are import errors.

`allowedValues` is an explicitly set-like collection. When present it must be
non-empty, exact typed duplicates are invalid and normalization orders values by
their exact typed representation. Source order does not affect
normalized-manifest equality. Every entry must satisfy the definition's other
constraints. An optional parameter `description` has at most 2048 Unicode scalar
values.

Regular-expression constraints are deferred. Adding `pattern` requires an
explicit dialect, timeout policy and agreement between JSON Schema and runtime
validation.

### Assignments are independent versioned operator state

An operator value is represented separately from the manifest:

```text
ParameterAssignment
|- ParameterId
|- Scope
|- TypedValue
|- Revision
`- Provenance
```

Assignments do not change ManifestRevision identity and do not participate in
normalized-manifest equality. A persisted implementation later supplies exact
assignment revisions; M2.1 uses immutable in-memory assignments and revisions to
exercise the contract.

### Resolution precedence is parameter-specific

Effective parameter values use this order:

```text
manifest default or defaultExpression
    -> Environment assignment
    -> Application assignment
    -> Component assignment
```

The last present valid assignment wins. Plugin and JSON Schema defaults are not
parameter layers; they belong to Resource, Workload, Binding or other extension
contracts and follow their own normalization rules.

An Application assignment supplies the default effective value for every
Component. A Component assignment changes the value only while rendering that
Component's Configuration documents. It cannot affect another Component or an
Application-owned Resource.

### M2.1 parameter use is limited to Configuration documents

The `parameters` Liquid map is available to structured and raw-text
Configuration document templates. M2.1 does not evaluate Liquid automatically
inside Resource `properties`, Workload `properties`, Binding `options` or
Resource export declarations.

Expanding templating into a plugin-owned payload requires a separate typed
expression contract. Arbitrary JSON must not silently become a template merely
because one of its strings contains Liquid syntax.

The canonical parameter access form is bracket notation:

```liquid
{{ parameters["query-prefix"] }}
```

It works for kebab-case IDs without depending on member-access behavior. A
reference to an undeclared or unresolved parameter fails materialization; it
never renders as an empty string.

### Resolved values retain provenance

For each Application and Component scope, planning produces an immutable
`ResolvedParameterSet`. Every entry contains a typed value and one origin:

```text
ManifestLiteralDefault
ManifestDefaultExpression
EnvironmentAssignment
ApplicationAssignment
ComponentAssignment
```

The Plan records the exact assignment revisions, deterministic expression
context and resolved non-secret values. Apply, reconciliation and audit use that
snapshot and never re-read a mutable current assignment.

### Manifest updates do not overwrite operator ownership

A new ManifestRevision may change a definition or default. The new default
affects only scopes without an applicable operator assignment. Existing
assignments remain operator-owned and continue to win when they remain valid.

Reset removes an assignment and returns the scope to the current lower-precedence
value. Haby never copies a manifest default into assignment storage or silently
converts a default into an operator value.

An assignment whose ParameterId is absent from the selected ManifestRevision is
not silently ignored. Planning returns a stable
`parameter-assignment-definition-not-found` error so an operator can explicitly
remove or migrate the orphaned assignment.

### Parameters never carry secrets

Application parameters are non-secret values that may appear in validation
diagnostics, previews and Plans. M2.1 has no `secret` parameter kind and no
`sensitive` flag that merely hides an ordinary parameter.

Credentials and tokens use typed SecretRefs and the secret boundaries defined
by ADR-006. They never enter the ordinary parameter resolver or Liquid context.

### Diagnostics belong to import or planning

Import uses standard Haby diagnostics such as:

```text
manifest.parameter.type-invalid
manifest.parameter.default-type-mismatch
manifest.parameter.constraint-invalid
manifest.parameter.default-expression-invalid
```

Planning or materialization uses stable application errors such as:

```text
parameter-value-required
parameter-assignment-type-mismatch
parameter-assignment-definition-not-found
parameter-default-expression-failed
parameter-default-expression-type-mismatch
parameter-reference-not-found
```

Planning failures do not mutate the accepted ManifestRevision or its historical
warnings.

### M2.1 uses in-memory resolution boundaries

The first implementation includes typed definitions and constraints,
literal/default-expression validation, `EnvironmentKey`, a pure in-memory
resolver, immutable fake assignment revisions, `ResolvedParameterSet`,
provenance and Configuration-document Liquid integration.

PostgreSQL assignment persistence, assignment-management APIs, UI and general
operational overrides remain later vertical slices.

## Consequences

- Literal strings can contain Liquid delimiters without being executed.
- Legacy environment-derived defaults have an explicit migration path through
  `defaultExpression` and `environment.key`.
- Import remains independent from an Environment.
- Defaults and operator assignments have unambiguous ownership and precedence.
- Parameter resolution is exact, typed, reproducible and explainable.
- Component overrides cannot leak into another Component or mutate Resources.
- Plugin-owned JSON remains ordinary data rather than an implicit template.
- Secrets cannot enter plans and diagnostics through an ordinary parameter.

## Rejected alternatives

### Interpret every string default as Liquid

This makes literal template delimiters impossible to express safely and hides
whether a value depends on planning context.

### Add a required flag

The combination of `required`, `default` and `defaultExpression` permits
contradictory states. Absence of either default already expresses the rule.

### Allow defaults to reference other parameters

This introduces dependency ordering, cycles and scope-dependent evaluation
before a demonstrated use case requires that complexity.

### Read process environment variables from Liquid

Implicit process state is neither portable nor reproducible and can expose
secrets accidentally. Operator assignments are the explicit input boundary.

### Apply parameters inside every arbitrary JSON value

This would turn plugin-owned schemas into a second implicit template language
and make validation order and normalized equality unpredictable.

### Store manifest defaults as operator assignments

Copying defaults into mutable storage loses provenance and causes later manifest
updates to overwrite or conflict with administrator intent.

### Mark ordinary parameters as sensitive

Masking changes presentation but does not provide SecretRef storage, access
control, versioning or audit semantics.

## Follow-up work

- implement the source-generated parameter-definition union and checked-in JSON
  Schema branches;
- implement exact scalar value objects, constraints and diagnostic tests;
- implement deterministic default-expression symbol validation and evaluation;
- implement in-memory assignment precedence, provenance and stale-input tests;
- integrate resolved values with structured and raw Configuration document
  rendering without enabling templates in plugin-owned JSON;
- add persisted assignment revisions and management APIs in a later vertical
  slice without changing the M2.1 resolution semantics.
