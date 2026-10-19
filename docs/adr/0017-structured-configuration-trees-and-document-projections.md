# ADR-017: Structured configuration trees and document projections

- Status: Accepted
- Date: 2026-10-13

## Context

ADR-009 separates Configuration documents from Components and requires Resource
outputs to enter a document only through explicit Document projections. M2.1
needs an exact representation and composition model that can produce
semantically equivalent JSON and YAML without allowing serialized plugin output,
implicit merge order or secret plaintext to leak into stored revisions.

Structured templates also retain Liquid value expressions. Without a precise
boundary, template evaluation could become a second structural composition
language and make JSON Pointer paths, validation and provenance unpredictable.

Profiles, operator overrides and secret delivery are later capabilities. The
M2.1 contracts must leave explicit extension points for them without implementing
their storage or execution now.

## Decision

### ConfigurationValue is an immutable format-neutral tree

Accepted structured content uses Haby-owned domain types rather than
`JsonElement`, `JsonNode`, `object` or a JSON/YAML library tree:

```text
ConfigurationValue
|- Null
|- Boolean
|- Number
|- String
|- Array
|- Object
`- SecretReference
```

`Number` reuses the exact arbitrary-precision JSON number semantics accepted in
ADR-015. `Object` has string keys compared ordinally. `Array` preserves order.
`SecretReference` is an internal typed placeholder and is not an object with a
magic JSON property. It contains the exact-version `SecretRef` defined by
[ADR-006](0006-secret-storage-and-encryption.md). Two `SecretReference` values
are equal when their `SecretId` and `SecretVersionId` both match; they do not
resolve or compare secret plaintext. The node does not carry Environment,
storage-backend details or an implicit `latest` selector.

The tree has no DateTime, binary or YAML-specific scalar variants. Manifest DTOs
may receive a structured template as `JsonElement`, but validation and mapping
produce immutable `ConfigurationValue` instances before domain composition.

### JSON and YAML share one semantic tree

Structured `json` and `yaml` Configuration documents use the same
`ConfigurationValue` model. The Configuration document selects the final
serializer; renderers and composition rules do not branch on output format.

JSON and YAML bytes need not match, but deserializing either authorized output
into the shared data model must preserve the same objects, arrays and scalar
types. Additional structured formats require an explicit serializer and
compatibility decision.

### Structured document templates have static structure

A Structured document template defines the static structure of a Configuration
document. Liquid expressions evaluate values inside that structure. Liquid
cannot dynamically create, remove, rename or reorder object members or array
elements. Conditional and dynamic structural composition is a separate
effective-configuration stage and is not a capability of structured template
evaluation.

Consequently:

- object keys are static;
- array length, element positions and order are static;
- Liquid is evaluated only in string value nodes;
- property names are never Liquid templates;
- evaluation cannot return an Object, Array or SecretReference.

Liquid control flow may affect the text computed for one value, but it does not
change tree topology. Profiles, patches, Resource projections and authorized
overrides own later structural composition.

Raw `text` documents remain full text templates and do not use this structured
template model.

### A standalone Liquid expression preserves scalar type

When a string value consists of exactly one Liquid output expression, its result
is converted to a typed scalar `ConfigurationValue`: String, Boolean, exact
Number or Null. An expression mixed with literal text always produces String.

For example:

```text
"{{ parameters.replicas }}"     -> Number(3)
"{{ parameters.enabled }}"      -> Boolean(true)
"worker-{{ parameters.index }}" -> String("worker-3")
```

An Object, Array or SecretReference result is rejected with a standard Haby
diagnostic because it would violate the template's static structure. Liquid
evaluation is pure and cannot create or mutate persisted values.

### Resource renderers return typed fragments

A Configuration renderer is distinct from structured template evaluation. It
performs a pure transformation of typed Resource outputs and renderer inputs and
returns a `ConfigurationFragment` containing one `ConfigurationValue`.

A fragment may be an Object, Array, scalar or SecretReference. A renderer:

- does not emit serialized JSON or YAML;
- does not select a Configuration document or insertion path;
- does not receive the complete target document for arbitrary mutation;
- does not resolve secret plaintext;
- produces the same semantic fragment for JSON and YAML targets.

The Binding's Document projection selects the target document, JSON Pointer path
and versioned renderer reference.

### Projection paths use a restricted RFC 6901 subset

`DocumentProjection.path` is an absolute RFC 6901 JSON Pointer over the shared
tree. Pointer segments use standard `~0` and `~1` escaping and are stored in one
canonical decoded-and-reencoded representation.

M2.1 projections support object-member traversal only:

- the root pointer is not a valid projection target;
- traversal through an Array is rejected;
- array indexes and append operations are not supported;
- missing intermediate Object nodes are created;
- an existing non-Object intermediate node is a collision;
- the target leaf must not already exist.

Numeric and `-` segments remain valid Object member names when their parent is
an Object; they never acquire array semantics in M2.1.

### M2.1 collision handling fails closed

M2.1 has no projection merge or overwrite policy. Composition fails when:

- the template already contains the projection leaf;
- two projections target the same canonical path;
- projection paths overlap as ancestor and descendant;
- an existing scalar or Array blocks intermediate traversal;
- a projection attempts to target a raw `text` document.

Sibling projections under the same existing or newly created Object are valid.
Fragments are never recursively merged merely because both sides are Objects.
Binding declaration or enumeration order cannot resolve a collision.

Versioned conflict policies are deferred until a concrete use case exists; the
M2.1 manifest does not expose a speculative `conflictPolicy` member.

### Projection composition is deterministic and all-or-nothing

For one structured document, Haby:

1. receives the exact `ResolvedParameterSet` accepted by planning, maps the
   static document template to a tree and evaluates Liquid values;
2. evaluates every referenced renderer into a typed fragment;
3. canonicalizes all projection paths;
4. validates the complete path set and current tree for collisions;
5. orders projections by canonical path using ordinal comparison;
6. creates permitted missing Object parents and inserts every fragment;
7. validates the completed tree;
8. serializes it through the selected versioned document serializer.

Failure at any step creates no Configuration-document revision. Sorting makes
execution and diagnostics deterministic; it does not establish precedence.

`documentProjections` is an explicitly set-like manifest collection. Exact
duplicates are rejected, and normalization orders entries by `documentRef`,
canonical path and `rendererRef` using ordinal comparison. Reordering source
entries therefore does not change normalized-manifest equality. Projection path
collisions are evaluated across all Bindings of the containing Component.

A structured template may have any ConfigurationValue root when it has no
projections. A document targeted by a projection must have an Object root; Haby
does not replace an Array or scalar root implicitly.

Parameter definitions, assignments and default-expression evaluation are
outside the document renderer. Their exact M2.1 resolution and provenance rules
are defined by
[ADR-019](0019-typed-parameters-and-effective-value-resolution.md).

### Import and planning validate different facts

Manifest import validates facts available without an Environment or renderer
execution, including:

- the referenced document exists in the consuming Component;
- the document is structured rather than raw text;
- the renderer reference and JSON Pointer are syntactically valid;
- statically known projection paths do not duplicate or overlap.

Planning or materialization validates Environment-specific facts, including:

- renderer availability and its typed output;
- collisions between fragments and the effective tree;
- type-specific output constraints;
- authorization for SecretRefs and protected paths when those capabilities are
  introduced.

Both stages use the standard Haby diagnostic model, but a planning diagnostic
does not retroactively mutate ManifestRevision validation history.

### Raw text remains a separate document kind

A raw `text` Configuration document contains a Liquid text template rather than
a `ConfigurationValue` tree. It does not support Document projections,
structured profiles, JSON Pointer patches or structured overrides.

M2.1 does not expose SecretRefs or secret plaintext to a raw Liquid context. A
future requirement for `.env`, PEM or another secret-bearing text format needs a
separate typed secret interpolation and delivery contract. It must not be added
by passing plaintext through ordinary template variables.

### SecretReference survives until authorized delivery

A stored structured Configuration-document revision retains typed
SecretReference nodes. It never replaces them with secret plaintext or a magic
serialized object during composition.

- previews render a redacted placeholder;
- revision identity and fingerprints may include the referenced SecretVersion
  identity but never its plaintext;
- ordinary JSON and YAML serialization cannot materialize the value;
- only an authorized delivery operation resolves the reference transiently.

M2.1 defines the placeholder boundary but does not implement Secret storage or
delivery. Its `SecretReference` node holds the typed `SecretRef` and remains
unresolved during tree composition, equality checks and redacted preview.

### Serializer profiles are deterministic and versioned

The JSON serializer emits UTF-8, deterministic escaping, canonical exact numbers
and Object keys sorted ordinally. The YAML serializer emits one document with
string mapping keys, deterministic key order and quoting, preserved scalar types
and no anchors, aliases, custom tags or comments.

Line endings, indentation and trailing-newline behavior belong to an explicit
versioned serializer profile. Byte-level output is reproducible for one profile,
but semantic tree equality remains authoritative across JSON and YAML.

Structured YAML is an output format over the shared tree. Haby does not promise
round-tripping YAML comments, aliases, formatting or custom scalar tags.

### Provenance is indexed separately from values

`ConfigurationValue` contains values, not source metadata. Composition builds a
separate format-neutral provenance index from canonical JSON Pointer to a typed
`ConfigurationValueOrigin`.

M2.1 defines and exercises at least these origins:

- `ManifestTemplate`, including the ManifestRevision reference;
- `ResourceBinding`, including Binding and renderer references.

Profile revisions, parameter assignments and operational overrides add their own
typed origins when implemented. Provenance values do not contain secret
plaintext or arbitrary source fragments.

## Consequences

- One renderer and composition pipeline supports both JSON and YAML.
- Structured templates remain predictable and cannot hide dynamic document
  topology in Liquid execution.
- Typed standalone expressions preserve booleans, numbers and null values.
- Projection conflicts are independent of dictionary, plugin or execution order.
- SecretRefs remain typed and redacted until authorized delivery.
- Import can reject structural mistakes without executing plugins, while
  planning owns effective-tree validation.
- Provenance can explain final paths without coupling source metadata to every
  tree node.
- M2.1 deliberately cannot project into arrays, merge projection fragments or
  securely interpolate secrets into raw text.

## Rejected alternatives

### Use JsonNode, JsonElement or object as the domain tree

Those types cannot express a typed SecretReference cleanly, encourage runtime
type checks and couple domain semantics to a serializer representation.

### Let Liquid generate a serialized structured document

Dynamic text generation would hide topology, defer structural errors and make
path provenance and format-neutral rendering unreliable.

### Let renderers emit JSON or YAML

Format-specific renderer output duplicates serializers and can produce different
semantics for the same Binding.

### Merge projections in declaration order

The result would depend on collection ordering and make collisions difficult to
review or reproduce.

### Encode SecretRef as an ordinary JSON object

User content could collide with the sentinel shape, and ordinary serializers or
patches could accidentally expose or corrupt security-sensitive semantics.

## Follow-up work

- implement the ConfigurationValue union, exact scalar conversion and immutable
  Object and Array values;
- implement canonical JSON Pointer parsing and M2.1 projection validation;
- add deterministic JSON and YAML serializer profiles with semantic equivalence
  tests;
- add focused tests for static structured templates, typed Liquid scalars,
  collisions, raw-text rejection and redacted SecretRefs;
- specify Profile, patch and override structural composition in ADR-011.
