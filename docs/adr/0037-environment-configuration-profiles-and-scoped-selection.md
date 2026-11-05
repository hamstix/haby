# ADR-037: Environment Configuration Profiles and scoped selection

- Status: Accepted
- Date: 2026-11-05
- Scope: Minimal Profile selection and expansion for the usable alpha renderer
- Extends: ADR-017/018/033/035/036

## Context

Mandatory individual assignments and Binding mappings for every Component make
initial deployment impractical for hundreds of Applications. An Environment
normally shares document layout, logging/telemetry defaults and connection
formatting conventions. Components still consume different explicit Resources.
Logging structures also vary: a Serilog MinimumLevel can be a String or a nested
Object with different category overrides. Common code must not interpret those
structures or require a special logging parameter language.

## Decision

### Environment defaults and explicit consumption

An Environment selects a set of exact ProfileRevisionRef values, each containing
profileId and revision. New Application planning automatically considers that
set for its Components. No individual assignment is required for the ordinary
case. Selectors, groups and automatic rollout remain deferred.

A Profile is a reusable configuration-layout contract, independent from Provider
instance configuration, Resource privilege policies and Workload profiles. The
existence of a Provider never inserts a connection. An explicit Component Binding
still determines the Resource consumed; Profiles cannot create Bindings, choose
Providers, grant access or infer a Resource from its name.

The set may contain separate document-layout, Serilog, OTEL, PostgreSQL-client,
RabbitMQ-client, Redis-client and authentication-client Profiles. These are opaque
Profile contents and adapter contracts; shared selection/storage/expansion must
not branch on those names or contain Provider-specific fields.

### Scoped selection replaces whole Profiles

Use Environment -> Application -> Component precedence, independently per ProfileId.
Each scope explicitly selects an exact revision or disables that ProfileId. A
scope can add a ProfileId absent from its parent. Omission inherits the previous
decision; no implicit latest reference. Duplicate decisions for one ProfileId at
one scope are invalid, even when identical. Input order does not choose a winner.

Resolve this effective set before expanding any Profile. At most one revision of
each ProfileId participates. A Component selection of serilog-defaults revision 4
replaces inherited revision 3 completely, allowing a wholly different Serilog
subtree. Revision 3 never contributes to that composition. Another ProfileId does
not implicitly replace it: explicitly disable the inherited ID before adding an
alternative that writes the same path. Otherwise normal collision rules apply.

There is no JSON path priority based on assignment scope. This is whole-Profile
selection, not structural merging. Keep Serilog and OTEL separate when they need
independent replacement. Profile parameters, including special minimum-log-level
parameters, are deferred; static alternate revisions cover the first scenario.

### Documents and independent contributions

A Profile can define documents, static contributions and Binding contribution
rules. Document definition is separate from contributions: one selected source
defines each Component-local DocumentId, artifact fileName, format and base
template. Other Profiles may refer to that DocumentId. Application documents also
participate in this uniqueness check. Duplicate definitions are errors even if
identical; fileName uniqueness follows the existing Component document contract.

The initial executable layout is static JSON. For example, one Profile defines
settings/appsettings.json with template {}, another contributes /Serilog, another
contributes /OpenTelemetry, and connection rules contribute their own leaf paths.
There is no need to declare empty parent objects: the composer creates them.

A static contribution supplies a target DocumentId, canonical non-root JSON
Pointer and ordinary immutable ConfigurationValue fragment. It applies for each
Component receiving that effective Profile. Profiles cannot embed trusted
SecretReference/DeferredValue nodes; renderers create confidential fragments later.

A Binding rule declares a logical slot, exact renderer ID, typed consumer defaults
and one or more document/path projections. It contributes only when a Component
Binding explicitly requests that slot. An unused rule contributes nothing and
is not a missing-Binding error. Do not guess slot membership from Binding names,
Resource names, Provider instances or compatible contract alone.

At most one Binding requests a given slot in one Component in this initial model.
One selected rule owns that slot and can have multiple projections. Missing or
ambiguous rules for a requested slot are errors. Different purposes, such as
main-database and reporting-database, use different slots. Only local-resource
targets are admitted by the first executable rendering path under ADR-036.

Slot match is not proof of output/renderer compatibility. Before accepting actual
rendering inputs, verify the exact output and renderer contracts through the
adapter metadata. No rendering permission is granted by Profile membership.

Expand all selected sources into one effective document/projection set, retaining
original ManifestRevision unchanged. Validate cross-Profile document references,
definition uniqueness, file names, slots and the complete path set. Reuse ADR-017
and the #125 composer semantics: missing Object parents are allowed; existing
leaves, duplicate/overlapping paths and non-Object traversal refuse. No recursive
merge, overwrite, enumeration-order priority or partial expansion success.

### Consumer defaults and provenance

Keep ADR-036 precedence: renderer defaults < selected rule defaults < explicit
Component Binding.options. Combine options by top-level property; a higher-level
property replaces the lower value as a whole. Do not recursively merge nested
Objects/Arrays. Absence inherits; null is a value whose validity is adapter-defined,
not a deletion instruction. Effective options must pass the exact adapter contract
and security constraints before rendering inputs are accepted.

No extra options layer on the scope-selection decision is needed. Shared options
remain in the selected Profile; consumer exceptions remain in Binding.options.
Profiles cannot replace output-derived endpoint/database/user/SecretRef identities
or change privileges through consumer settings.

Retain immutable provenance separately from values: effective Profile references,
selection scope, document/contribution origin, slot-to-Binding association and
source of each effective option (Profile or Binding; renderer defaults when later
resolved). Diagnostics contain safe identities/paths and standard codes, never
source values, secret material or raw exception text.

### Frozen planning inputs and lifecycle

Profiles have distinct typed portable IDs, using existing ApplicationId lexical
rules without mandatory reverse-DNS; revisions are positive integers. Identity
is ProfileId + revision. Equal normalized content under that identity is an
equivalent retry; differing content is a conflict, never overwrite.

Planning fixes the exact effective Profile revisions, scope decisions, slot
associations and effective options. Environment or scoped-selection changes affect
new Plans; accepted Plans/generations do not re-read mutable defaults. Updating
existing deployments requires a new accepted desired state and future rollout
support. The current generation-1 pilot is not expanded by this decision.

Persist future Profiles/selections/document revisions through shared generic
envelopes and typed versioned payloads under ADR-035, not Provider-specific tables,
EAV or magic user JSON. Profile removal must later respect pinned accepted inputs;
this decision does not authorize deletion or a new cleanup API.

### First implementation slice and explicit wire boundary

Implement immutable typed Profile revisions, Use/Disable scope decisions,
Component Binding slot associations and pure deterministic effective-layout
expansion first. Inputs are already parsed typed values and an exact in-memory
Profile catalog. Return static fragments and unresolved Binding render requests,
with all-or-nothing diagnostics and provenance. Test composition through the
existing composer using synthetic typed fragment producers.

The original individual-ProfileAssignment JSON example is superseded as a design
direction, not a published implemented API. The exact new Profile/selection JSON
schema and the Application field/API version for declaring a Binding slot require
a focused contract before that boundary is implemented. Do not add fields to the
current manifest schema, silently reinterpret an existing field, or implement
HTTP/gRPC/CLI operations in this first slice. In-memory slot associations model
future author-declared slots; they are not mandatory per-Component operator mappings.

Do not claim that expansion alone verifies real output availability, registered
renderer execution compatibility, or adapter option constraints. Carry exact
identities and options to that later gate. Missing exact Profile revisions,
ambiguous slots and invalid structural composition already refuse here.

No persistence, planning integration, actual renderer/formatter execution,
secret resolution/delivery, Liquid, YAML/text execution, parameters, selectors,
group assignments or rollout. These follow narrow subsequent slices. No new
package or Provider-specific persistence is authorized.

## Required evidence

- Environment-only defaults work for several Components with different explicit
  Bindings, without individual assignments or inferred connections.
- Static logging/telemetry contributes to each Component; connection contributions
  appear only for its requested slots. An unused rule is harmless.
- Environment -> Application -> Component replacement/disabling/addition selects
  exactly one revision per ID, independently from input enumeration order.
- A whole Serilog contribution switches between scalar/object MinimumLevel forms
  by revision selection; an independent OTEL Profile remains unchanged.
- Different Profile IDs writing /Serilog conflict unless the inherited one is
  disabled. Two definitions of a document are errors even if identical.
- Missing revisions/documents/rules, duplicate Binding slots, ambiguous rules,
  colliding paths/file names and incompatible roots refuse without partial layout.
- Effective consumer options and provenance reflect top-level replacement;
  fixtures prove nested objects are not silently deep-merged.
- A second non-PostgreSQL typed contribution follows the actual shared expansion
  and composer seam. No common model knows database, queue or logging fields.
- Inputs, outputs and provenance are immutable; errors are deterministic/bounded
  and value-free. Existing import, composition and provisioning tests remain valid.

## Consequences

The normal operator path is configure Environment once, import Applications, then
Plan/commit/execute with automatically selected defaults. Local selections are
exceptions. The first slice proves selection/expansion mechanics without promising
wire support or secret delivery. It supersedes ADR-036's earlier deferral of all
Environment/global assignment, while retaining deferred selectors, broad overlapping
target sets, arbitrary patches and mass rollout.
