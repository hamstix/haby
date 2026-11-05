# In-process Environment Profile expansion

Implemented for issue #126 under [ADR-037](../adr/0037-environment-configuration-profiles-and-scoped-selection.md).
The subsequent [Profile/assignment JSON boundary](configuration-profile-json-v1alpha1.md)
implements issue #128 under ADR-038. Issue #130 adds `configurationSlot` to the
current Application v1alpha1 and normalized snapshots. Expansion remains a pure
semantic API without Plan integration, Host operation or habyctl command.
[Issue #132](../development/132-profile-revision-persistence.md) adds separate
in-process Profile import/exact read and PostgreSQL persistence; loaded declarations
can populate this exact catalog. Profiles cannot yet be configured through Host/habyctl.
Superseded Application snapshots require an explicit fresh catalog; see the
[reset requirements](../development/130-binding-configuration-slot.md).

## Inputs and identities

Domain `ProfileId` follows ApplicationId lexical rules (1-128 lowercase ASCII
characters, initial letter, non-adjacent dot/hyphen separators). A
`ProfileRevisionRef` pairs it with a positive integer revision. There is no
latest/alias lookup. `ConfigurationProfileRevision` snapshots documents, static
contributions and rules. `ConfigurationProfileCatalog` is an immutable exact
in-memory snapshot: structurally equal submissions at one identity are equivalent;
conflicting submissions throw a fixed, value-free `ArgumentException`. Collection
order does not affect equality; multiplicity and every content field do.

A `ProfileDocument` defines a Component-local document ID, artifact file name,
format and already-static semantic template. Artifact names follow the current
manifest's basename rules and are unique ordinally across effective documents.
Only JSON executes in this slice. `ProfileStaticContribution` identifies its
source, target document, canonical non-root JSON Pointer and ordinary immutable
ConfigurationValue plus immutable explicit named-secret projection declarations.
Expansion refuses those declarations with `profile.secret-projections.unsupported`
until the separately authorized planning/delivery capability exists. Contribution IDs and rule IDs are unique within their
respective Profile collections; Application static contribution IDs are also unique.

`ProfileBindingRule` declares a local slot, exact opaque ConfigurationRendererId,
ConfigurationObject consumer defaults and one or more document/path projections.
These are semantic typed trees, not serialized adapter arguments. Common code does
not interpret option names or specialize on renderer/resource families.

`ProfileComponentInput` accepts an existing immutable `NormalizedComponent`,
Component decisions and optional already-static Application contributions.
Slot associations come exclusively from `NormalizedBinding.ConfigurationSlot` in
the supplied Component, including Components read from persisted revisions. No
caller-supplied mapping or inferred association exists. Duplicate requested slots
are checked again during expansion for restored semantic inputs. Existing
Application documents and direct Binding document projections participate as ordinary sources.
No Resources, Providers or Bindings are created or inferred.

## Selection and expansion

Call `ConfigurationProfileExpander.Expand(environment, application, components)`.
Each scope contains `ProfileDecision.Use(exactReference)` or
`ProfileDecision.Disable(profileId)`. Omission inherits. Environment, Application,
then Component decisions replace the whole decision independently per ProfileId.
A nearer scope can add, disable or re-enable an ID. Duplicate decisions at one
scope refuse even if identical. Selection finishes before exact revision lookup;
an overridden or disabled revision is not loaded or expanded.

Each Component gets its effective Profiles without an individual assignment.
One source owns each document definition, including Application definitions;
identical duplicate definitions still refuse. Independent Profiles may contribute
to the same document but cannot overwrite each other. Only an explicitly requested
slot emits a Binding render request. Unused rules emit nothing. Each requested
slot must have one Binding association and one selected rule. Missing or ambiguous
rules refuse. Only local-resource targets are admitted for emitted requests.

For each request, rule defaults are replaced by explicit Binding.options at the
top property level. Nested Objects/Arrays are whole values, and null is a value,
not a deletion instruction. Renderer defaults and actual adapter schema/security
validation are deferred. Exact renderer and Binding identities and effective
options remain available for that later gate; slot matching grants no rendering
permission and proves no actual output availability.

All emitted static and Binding projection targets are checked together against
their document template using the existing ConfigurationComposer structural
checker. It validates canonical pointers, existing leaves, Object-only traversal
and duplicate/ancestor conflicts, allowing missing Object parents. Empty and
escaped/numeric Object keys retain the existing composer semantics. Unresolved
requests reserve paths without producing placeholder fragments or fake successful
documents. Missing emitted document references refuse. Profile templates,
contributions and consumer defaults cannot embed SecretReference or DeferredValue,
including nested nodes; trusted renderers may produce them later.

Inputs are already-static semantic values. This API performs no expression or
parameter evaluation. Callers must materialize supported inputs and explicitly
refuse unsupported evaluation before invoking this boundary. YAML/text execution,
output retrieval, renderer execution, secret resolution/delivery and rollout are
not implemented.

## Immutable results and provenance

Success returns complete Component layouts with documents, static contributions,
and unresolved `ProfileBindingRenderRequest` values. It never rewrites the
ManifestRevision. Results retain effective decisions (including disables), all
original scope decisions, exact Profile references, scope, document/contribution
origin, the actual Binding/slot association, renderer, options, and per-property
Profile/Binding option origin. Renderer-default provenance belongs to the later
adapter gate. Caller collections cannot mutate the catalog, inputs or results.
Result collections are immutable snapshots, ordered by safe identities/paths;
semantic Objects retain ordinal key equality rather than enumeration precedence.

To compose after a future authorized renderer gate, select one document's static
contributions and ready renderer fragments and pass them to ConfigurationComposer.
The acceptance tests do this with two synthetic typed output/render families,
including api/worker sharing an exact SecretRef with different consumer options.
Production expansion does not execute those test producers. The existing separate
ConfigurationJson preview redacts the entire resulting confidential nodes.

## Standard diagnostics

Every diagnostic is an error containing only Code, Component, Document, canonical
Path and safe Source. Invalid raw pointers are omitted (Path is empty). Source
uses a Profile ID/revision/local source ID or an Application local source ID;
selection errors identify scope/Profile. Artifact names, node values, option
values, formatter Arguments, SecretRefs and exception text are never copied.

| Code | Meaning |
| --- | --- |
| `profile.selection.duplicate` | Same ProfileId decided more than once at one scope. |
| `profile.revision.not-found` | Exact effective Profile revision absent. |
| `profile.component.duplicate` | Component ID supplied more than once. |
| `profile.document.duplicate` | More than one definition of a Component document. |
| `profile.document.file-conflict` | Effective document artifact names collide. |
| `profile.document.invalid-file-name` | Artifact name violates existing basename rules. |
| `profile.document.unsupported` | Non-JSON document execution requested. |
| `profile.document.not-found` | Emitted contribution/request targets an absent document. |
| `profile.contribution.duplicate` | Duplicate static contribution identity in one source. |
| `profile.rule.duplicate` | Duplicate rule identity within a Profile. |
| `profile.binding.unsupported` | Emitted Binding target is not local-resource. |
| `profile.slot.duplicate` | More than one association requests the same slot. |
| `profile.slot.not-found` | Requested slot has no selected rule. |
| `profile.slot.ambiguous` | Requested slot has several selected rules. |
| `profile.secret-projections.unsupported` | Effective contribution requests secret insertion not yet supported by materialization. |
| `profile.value.unsupported` | Static semantic input contains a trusted confidential node. |

The structural checker additionally returns the existing
`render.projection.invalid-path`, `render.projection.template-collision` and
`render.projection.path-conflict` codes. Sort ordinally by Component, Document,
Path, Code, Source and deduplicate exact diagnostic tuples. The first 100 errors
are returned, with complete distinct ErrorCount and IsTruncated. Any error sets
Layouts to null, including an error in just one Component; truncation never
conceals failure or publishes a partial layout. These diagnostics do not mutate
historical manifest import warnings.
