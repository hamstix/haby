# ADR-009: Application manifest domain model

- Status: Accepted
- Date: 2026-09-22

## Context

The legacy Haby model overloads a configuration key as a runnable service part,
configuration document, resource-sharing identity and deployment description.
Resources, generated credentials and rendered JSON are consequently difficult
to rename, share, reconcile and delete safely.

M2 needs a stable domain vocabulary and a predictable `haby.json` contract
before persistence, plugin and API implementations are replaced. The public
contract must work with `System.Text.Json` source generation and must not depend
on reflection-based type discovery. It must also support the existing case where
a Component consumes a Resource owned by another Application without gaining
control of that Resource.

## Decision

### Application is the manifest and ownership boundary

One manifest declares one Application identified by `metadata.id`. The stable
identity is independent from display name, folder path and source repository.

All manifest-local IDs use lowercase kebab-case. Application, Component,
Resource, Resource export, Binding, Configuration document and Workload
identities are stable. Renaming display metadata or moving an Application does
not replace those objects.

Resources are declared only in `spec.resources`. The declaring Application is
their owner, so the contract has no `ownerRef`. A Resource used by one Component
is still Application-owned. Bindings express consumption, not ownership.

### Core concepts remain separate

- A **Component** is a runnable software part of an Application.
- A **Configuration document** is a named materialized artifact consumed by a
  Component.
- A **Resource** is a managed dependency such as a database, principal, queue or
  bucket.
- A **Binding** connects a Component to a local Resource or an external Resource
  export and optionally projects typed outputs into a Configuration document.
- A **Workload** is a portable declaration of how one Component should run.
- A **ManagedWorkload** is the Environment-specific desired and observed state
  produced from one Workload declaration.
- A **RuntimeInstance** is a concrete running replica, task or process produced
  for a ManagedWorkload.

Workload is a separate domain declaration rather than a Resource subtype or an
unstructured Component value. Its manifest declaration is nested inside the
Component for authoring convenience, making the parent relationship structural
and removing a redundant `componentRef`. Each persisted ManagedWorkload retains
its own immutable ID, desired and observed state and operation history. Resource
and ManagedWorkload implementations share durable operation infrastructure but
use distinct domain contracts.

RuntimeInstance is conceptual and observed rather than a required persisted
aggregate in M2. A later runtime delivery or observability capability may append
redacted per-instance audit events without requiring Haby to maintain a durable
inventory of every replica.

A Workload local ID is scoped to its containing Component. A Workload cannot be
shared or exported and has no meaning without that Component. Component removal
therefore plans removal of its nested Workloads; this cascade does not apply to
Application-owned Resources. Moving a Workload declaration between Components
is replacement unless an explicit migration maps it to existing persisted state.

Configuration document declaration does not imply delivery. Delivery channels
and publication policy are configured separately, so the Application manifest
does not contain a `publish` Boolean.

### Resource lifecycle is declaration-driven

A Resource remains desired while it is declared in `spec.resources`. Binding
count and Component existence do not control that lifecycle. Removing a
Component removes its consumption relationships but never implicitly removes,
orphans or deprovisions a Resource that is still declared.

Resource lifecycle has no Component-linked `ownerRef`,
`lifecycle.componentRef`, `deleteWith` or equivalent shortcut. To remove a
Component-specific Resource, the author explicitly removes both declarations.
The resulting plan removes Workloads, revokes Bindings and consumer-specific
access, verifies exports and other consumers, and only then applies the Resource
deletion policy.

`deletionPolicy` controls what happens after explicit Resource removal; it does
not determine when the Resource leaves desired state. If a change removes the
last known consumer from a still-declared Resource, that Resource remains managed
and the plan produces an unused-resource warning rather than deleting it.

### Types and references are explicit

Resource and Workload type identifiers include a contract version, for example
`postgresql.database/v1alpha1` and `kubernetes.workload/v1alpha1`.

Compound references use structured DTOs rather than delimiter-based strings.
In particular, a Profile assignment identifies an exact immutable revision with
`ProfileRevisionRef { profileId, revision }`, where `revision` is a positive
integer. The persisted contract does not accept `profile-id@3`, `latest` or a
version range. User interfaces may resolve a selection to an exact revision
before validation and persistence.

Binding targets are a discriminated union:

- `local-resource` contains a local `resourceRef`;
- `application-export` contains an `applicationRef` and `exportRef`.

Document projections are an explicit list on a Binding. Each projection targets
a Configuration document at a JSON Pointer path and selects a versioned
renderer. The referenced document determines the output format, so a Document
projection needs no discriminator. Resource outputs never enter a document
merely because the Resource exists.

Configuration document definitions use `format` as their discriminator. JSON
and YAML definitions contain structured templates; text definitions contain raw
string templates. These are explicit source-generated CLR alternatives rather
than shapes inferred from the `template` token.

Core manifest alternatives use `JsonPolymorphic` and explicitly registered
`JsonDerivedType` contracts. The serializer context uses metadata-based
`System.Text.Json` source generation, which supports polymorphism. Manifest DTOs
do not use `object`, reflection-based discovery or converters that guess a type
from JSON shape.

Plugin-owned Resource and Workload `properties`, structured document templates
and typed parameter defaults use `JsonElement` at documented extension
boundaries. Their token kinds and contents are validated against the
corresponding versioned schemas; they are not used for CLR type selection.

### Structured documents are format-neutral

JSON and YAML Configuration documents share one structured configuration tree,
including internal typed SecretRef placeholders. Renderers return a typed tree
fragment rather than serialized text. Haby applies Document projections,
profiles and overrides to that tree using JSON Pointer paths, then serializes the
completed document according to its `format`. SecretRefs remain typed revision
data until an authorized delivery operation materializes their values.

Structured YAML is limited to the shared JSON-compatible data model: mappings
have string keys, duplicate keys and custom tags are invalid, and comments,
anchors, aliases and source formatting are not preserved. Scalar types are
preserved and ambiguous strings are quoted deterministically.

Raw `text` documents remain Liquid templates and do not support Document
projections, structured merge or JSON Pointer insertion. A renderer cannot
serialize the final document or vary its semantic fragment between JSON and
YAML.

### Cross-application consumption uses exports

A Component cannot reference another Application's internal Resource directly.
The owner publishes a stable Resource export containing:

- a local `resourceRef`;
- a versioned output contract;
- an optional provider-defined access profile.

The consumer binds to the export through an `application-export` target. This is
read-only at the Haby control-plane level: the consumer cannot change desired
state, provider selection, lifecycle, reconciliation or commands for the owned
Resource.

An external-system access profile, such as PostgreSQL read-only grants, is a
separate provider concern. A consumer cannot request broader access than the
owner exported. Sharing a Resource does not require sharing credentials; a
provider may issue consumer-specific principals and SecretRefs.

An export is not authorization by itself. Environment policy decides which
Applications may consume it. Installation-specific allow-lists do not belong in
the portable repository manifest.

The owner remains responsible for Resource lifecycle. Removing a consumer
removes only its Binding and consumer-specific access state. Removing an export,
Resource or owning Application with active consumers requires an impact-visible
plan and is blocked unless dependencies are resolved or an authorized
destructive operation is approved.

## Consequences

### Positive

- Parser and JSON Schema alternatives map directly to closed CLR types.
- Source-generated deserialization does not require runtime assembly scanning.
- Folder moves and display-name changes do not alter ownership or references.
- Components can share local Resources without copying rendered JSON.
- Cross-application sharing has an owner-controlled, versioned compatibility
  boundary.
- Component removal cannot accidentally deprovision a still-declared Resource.
- ManagedWorkload lifecycle, scale and observed state can evolve independently
  from Resource and Configuration document contracts.
- Manifest authors see Workloads next to the Component they run without
  repeating a `componentRef`.
- Configuration exposure remains explicit and auditable through Document
  projections.
- One renderer and composition pipeline can produce equivalent JSON and YAML
  documents.

### Negative

- The manifest is more verbose than shape-dependent shorthand.
- Applications must maintain stable local IDs and explicit references.
- Resource-export compatibility and consumer impact require dependency tracking.
- Workload reconcilers and Resource provisioners need separate public contracts.
- The alpha contract requires compatibility tests before promotion to v1.

## Rejected alternatives

### Component-owned Resources through `ownerRef`

This makes ownership mutable and couples Resource lifecycle to one consumer. A
Resource is instead Application-owned; Component removal changes Bindings, and
Resource removal remains an explicit manifest and plan operation.

### Component-linked automatic Resource deletion

Fields such as `lifecycle.componentRef` or `deleteWith` make Component removal
contradict a still-present Resource declaration and are unsafe for shared or
exported Resources. The author must remove the Resource explicitly; planning
still provides safe dependency ordering and impact visibility.

### Direct references to another Application's Resource

This exposes implementation identity, provides no owner-controlled compatibility
contract and risks treating knowledge of an ID as permission. Resource exports
provide the required indirection and policy boundary.

### String-or-object shorthand for references

Accepting a string for local references and an object for external references
requires shape-dependent parsing and produces a less predictable generated
contract. Explicit discriminated targets are preferred.

### Delimiter-based compound references

Values such as `observability-defaults@3` require handwritten splitting and
mix identity with revision-selection syntax. A structured reference maps
directly to source-generated DTOs, JSON Schema and field-specific validation
errors while keeping applied revisions deterministic.

### Treat Workload as Resource

Workload declarations and ManagedWorkloads represent execution, scale and
suspension, whereas Resources represent managed dependencies consumed by
Components. Concrete Runtime Identity is a separate security concept. Sharing
only the durable operation infrastructure preserves useful reuse without
collapsing the domain concepts.

### Declare Workloads in a top-level manifest collection

A top-level collection requires every Workload to repeat `componentRef`, permits
dangling references and separates runtime configuration from the Component it
runs. Nesting is preferred for authoring while the parsed Workload remains a
separate domain declaration and its Environment-specific ManagedWorkload remains
a separate persisted entity.

### Infer publication from a document flag

A `publish` Boolean does not identify a destination, security policy or delivery
revision. Publication belongs to a separate delivery configuration and operation.

### Format-specific renderers for JSON and YAML

Allowing renderers to emit serialized text duplicates composition behavior,
makes path collision detection and provenance unreliable, and can produce
different semantics for equivalent formats. Renderers therefore return a shared
structured tree and document serializers own textual representation.

## Follow-up work

- publish the `v1alpha1` JSON Schema matching the discriminated CLR contracts;
- add parser and semantic-validation tests for every invariant;
- define environment authorization policy for Resource-export consumption;
- define compatibility rules for Resource export contract revisions;
- implement separate Resource provisioner and Workload reconciler abstractions.
