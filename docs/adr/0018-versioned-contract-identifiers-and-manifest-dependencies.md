# ADR-018: Versioned contract identifiers and manifest dependencies

- Status: Accepted
- Date: 2026-10-13

## Context

M2.1 needs stable identifiers for Resource types, Workload types, Resource
output contracts and Configuration renderers. These identifiers must survive
changes to plugin packages, assemblies and CLR types, and they must not imply a
runtime namespace-resolution mechanism that does not exist.

The Application manifest also permits a Component to consume a Resource export
owned by another Application. Import must remain portable and independent from
an Environment, while planning must resolve Provider aliases and
cross-Application references to exact reproducible inputs. The contracts must
therefore distinguish declaration validation from Environment-specific
resolution.

M2.1 establishes these boundaries without implementing persisted Provider
instances, PostgreSQL adapters or plugin execution.

## Decision

### Versioned contract names are opaque

A versioned contract identifier has the form:

```text
<contract-name>/<contract-version>
```

For example:

```text
postgresql.database/v1alpha1
postgresql.connection/v1alpha1
kubernetes.workload/v1alpha1
```

The complete contract name before `/` is declared and registered by the
contract author. Haby treats it as one opaque value. Dots and other permitted
separators inside the name are naming conventions only; Haby does not split
them into a publisher, family, plugin or resolution namespace.

The version after `/` is mandatory. Contracts never use `latest`, version
ranges or unversioned aliases. A later compatibility mechanism may describe an
explicit relationship between exact versions, but it does not change either
identifier.

M2.1 contract IDs use the following lexical form:

```regex
^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*/v[1-9][0-9]*(?:(?:alpha|beta)[1-9][0-9]*)?$
```

Manifest-local IDs use lowercase kebab-case with at most 63 characters.
Provider aliases have at most 128 ASCII characters and use:

```regex
^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$
```

These forms are validation rules only; separators do not participate in
resolution.

### Identifier types remain distinct

The shared lexical form does not make the identifiers interchangeable. Domain
and application contracts use separate strongly typed values, including:

```text
ResourceTypeId
WorkloadTypeId
OutputContractId
ConfigurationRendererId
```

A Configuration renderer is an executable capability rather than a Resource
output data contract. Sharing the versioned lexical form does not collapse
their categories or permit one identifier type to substitute for another.

### Contract identity is independent from implementation identity

A Contract ID is independent from plugin, assembly, package and CLR type
identity. A module may implement an existing Contract ID only by conforming to
its registered schema and semantics; it cannot redefine that contract.

Haby registers definitions and implementations separately:

```text
ContractDefinition
|- exact Contract ID
|- schema
|- semantic metadata
`- compatibility metadata

CapabilityImplementation
|- implementation identity
|- supported exact identifiers
|- capability kind
`- implementation factory
```

One exact Contract ID has one canonical normalized definition. Re-registering
an equivalent normalized definition is idempotent. Registering a different
schema or semantic definition for the same ID fails startup with a contract
definition conflict.

Conformance includes behavior and cannot be proven from a schema alone. Plugin
compatibility tests remain responsible for proving the declared semantics.

### Implementation cardinality depends on resolution mode

Provider-scoped capabilities may have several active implementations for one
exact Contract ID. The Provider instance selected from `providerRef` determines
the implementation, and planning verifies that it supports the requested exact
contract.

Capabilities without Provider-instance selection use singleton resolution. One
exact capability ID may resolve to at most one active implementation in the
composed Haby runtime. Multiple active implementations, including multiple
Configuration renderers for one exact `ConfigurationRendererId`, fail startup
with an implementation-ambiguity error.

A definition may exist without an active implementation. Absence becomes an
availability diagnostic when a manifest or plan requires that capability;
ambiguity is rejected at startup because it cannot be resolved deterministically.

### Provider references are Environment-local aliases

`providerRef` is a typed alias such as:

```text
postgresql.primary
```

Manifest import validates its syntax but does not require the alias to exist.
The same portable manifest may be imported before an Environment is configured
or applied to several Environments whose aliases select different Provider
instances.

Planning resolves an alias to an exact typed result containing the Provider
instance, Provider configuration revision and selected implementation identity
available in that implementation slice. The Plan stores this result and never
re-resolves the alias during apply, reconciliation or audit.

Changing an alias does not mutate an existing Plan or ApplicationDeployment.
Using the new resolution requires a new Plan and acceptance of the resulting
effective desired-state snapshot.

### Application-export targets state the consumer contract

An external Binding target has this shape:

```json
{
  "kind": "application-export",
  "applicationId": "shared-storage-service",
  "exportRef": "reporting-database",
  "contract": "postgresql.connection/v1alpha1"
}
```

`applicationId` is explicit and is not an alias or display name. The target does
not pin an Application version: the consumer depends on the owner's public
export contract, while a DeploymentPlan resolves the exact producer deployment
generation and ManifestRevision.

The consumer repeats the exact expected `OutputContractId` even though the owner
also declares it. This makes consumer intent independently reviewable, supports
Bindings without a Document projection and permits planning to reject a
contract mismatch without inferring requirements from a renderer.

M2.1 requires exact output-contract equality. Explicit compatibility rules
between different versions are deferred until a real contract-evolution case
requires them.

### Binding options describe consumption, not Resource desire

A Binding may contain an `options` extension value for consumer-specific input:

```json
{
  "target": {
    "kind": "local-resource",
    "resourceRef": "main-database"
  },
  "options": {
    "accessMode": "read-only"
  }
}
```

Binding options are separate from Resource `properties`. They cannot change the
Resource's desired state, Provider selection, ownership or lifecycle. Their
`JsonElement` content is validated against the exact binding or output-contract
schema registered in the metadata catalog.

For an Application export, options cannot request broader access than the
owner's export and Environment policy permit.

### Import does not resolve Environment dependencies

Import validates:

- identifier and alias syntax;
- local references and target kinds;
- registered contract definitions and schemas available to import validation;
- the internal consistency of the consumer declaration.

Import does not require an external Application, Resource export or Provider
alias to exist in an Environment. Release bundles may therefore import their
Applications in any order, and an identical declaration can be imported into
different installations.

Environment-specific absence, authorization and compatibility failures are
planning errors, not historical ManifestRevision diagnostics.

### Planning resolves exact producer generations

For every `application-export` Binding, planning selects its producer as
follows:

- when the producer has `DeployRevision` in the same ChangeSet, use that target
  ManifestRevision and the generation that the Plan proposes to accept;
- when the producer is not a ChangeSet member, use the exact desired snapshot of
  its current ApplicationDeployment;
- when the producer is being removed, has no deployment, lacks the named export
  or exposes a different contract, planning fails.

The producer ApplicationDeployment identity, generation, ManifestRevision,
export identity and exact contract become immutable planning inputs. A change
to any referenced input makes the Plan stale rather than changing its meaning.

### Bindings define the dependency graph

Haby derives the cross-Application dependency edge:

```text
producer Application -> consumer Application
```

from each `application-export` Binding. The manifest has no separate manual
`dependsOn` list that could disagree with the actual references.

M2.1 requires the resulting graph to be acyclic. A future capability-aware
operation planner may support strongly connected groups by separating Resource,
access, rendering and Workload phases, but the first side-effect-free planner
does not claim those execution semantics.

### ChangeSets need not include the complete dependency graph

A consumer change may use an existing exact producer deployment without adding
the producer as a ChangeSet member. Planning still records the producer
generation as an input.

Removing or incompatibly changing an export, its Resource or its owning
Application requires impact analysis of all known consumers. The Plan is
blocked unless affected consumers are updated or removed in the same ChangeSet,
or a future explicitly authorized destructive workflow resolves that impact.

### Resource exports have no independent revision in M2.1

An exact Resource export is identified by:

```text
owner ApplicationId
+ owner ManifestRevision
+ export local ID
```

M2.1 does not introduce a `ResourceExportRevision` aggregate. Independent export
revisioning can become another exact planning input later if export lifecycle
requirements justify it.

### Metadata and Environment resolution are separate abstractions

M2.1 defines a read-only `IManifestContractCatalog`-equivalent abstraction for
exact type, contract, renderer and schema metadata. It also defines an
`IProviderInstanceResolver`-equivalent abstraction that resolves one typed
Provider alias in one Environment to an exact result.

The metadata catalog performs no plugin side effects. M2.1 provides an in-memory
catalog for manifest-validation tests and a fake or in-memory Provider resolver
for planning tests. Full Provider instances, PostgreSQL persistence and plugin
implementation factories are not required in this slice.

Tests must prove that import is independent from an Environment and that a Plan
retains the exact alias and external-export resolutions it observed.

### Diagnostics follow their owning stage

Import uses standard Haby diagnostics such as:

```text
manifest.type.unknown
manifest.reference.not-found
manifest.reference.invalid
manifest.properties.schema-violation
```

Planning uses stable application errors such as:

```text
provider-alias-not-resolved
application-export-not-found
application-export-contract-mismatch
application-dependency-cycle
active-export-consumers
```

The same portable manifest may plan successfully in another Environment or
ChangeSet, so planning failures never mutate the accepted ManifestRevision or
its historical validation diagnostics.

## Consequences

- Public contract names survive plugin, package and CLR refactoring.
- Dots in names remain a human convention rather than hidden resolution logic.
- Contract-definition conflicts and singleton implementation ambiguity fail
  deterministically during startup.
- Provider implementations can vary by Environment without changing the
  portable manifest.
- Consumer intent names its exact expected export contract.
- External dependency resolution is reproducible without making import depend
  on deployment order.
- Cross-Application dependency order is derived from real Bindings.
- M2.1 can test the complete boundary with in-memory catalogs and resolvers
  before persistence and plugin execution exist.

## Rejected alternatives

### Parse a contract family or publisher from the name

This would turn a naming convention into a resolution protocol and constrain
contract authors without providing a required capability.

### Derive contract identity from a plugin or CLR type

Package, assembly and implementation names are deployment details and may
change without changing public schema or semantics.

### Let the consumer omit its expected export contract

Inferring compatibility from the current owner revision or renderer would hide
consumer intent and make Bindings without projections ambiguous.

### Resolve external Applications during import

This would make portable declarations depend on Environment state and import
order.

### Store dynamic selectors in a Plan

Re-resolving aliases or producer revisions at apply time would make an immutable
Plan change meaning after it was reviewed.

### Add manual dependsOn lists

Manually maintained dependency lists can diverge from the Bindings that create
the actual dependency.

### Introduce ResourceExportRevision immediately

The owner ManifestRevision already supplies the exact immutable boundary needed
by M2.1. A second revision aggregate would add lifecycle and persistence work
without a current independent-versioning use case.

## Follow-up work

- define the exact identifier lexical constraints in the checked-in M2.1 JSON
  Schema and typed value-object tests;
- add normalized contract-definition conflict and singleton ambiguity tests;
- implement in-memory manifest metadata and Provider-resolution test doubles;
- add ChangeSet planning tests for external producer selection, stale inputs,
  contract mismatches, dependency cycles and active consumers;
- add persisted Provider instances and plugin implementation adapters in later
  vertical slices without changing the M2.1 abstractions.
