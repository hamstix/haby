# ADR-036: Configuration rendering, minimal profiles and deferred formatting

- Status: Accepted
- Date: 2026-11-03
- Scope: Usable alpha configuration rendering after the five-step provisioning pilot
- Extends: ADR-009/017/018/019/034/035

## Context

The five-step PostgreSQL workflow provides confirmed connection metadata and an
exact generated SecretRef. Rendering and confidential delivery are not yet
implemented. Two Components can intentionally share one application-login while
requiring different client settings, for example a connection timeout on api.
Existing applications may expect one connection string rather than split fields.
Repeated document layout must be reusable without copying it into every manifest.

The maintainer accepts typed deferred formatting, consumer Binding.options and
minimal immutable Configuration Profiles for the first useful rendering track.
This supersedes the proposed split-fields-only pilot. It does not authorize a
plaintext endpoint on the unauthenticated operator API.

## Decision

### Exact rendering inputs and immutable result

Select an exact ApplicationDeploymentId, generation, ComponentId and DocumentId.
The initial pilot requires the requested generation to be fully applied, with
all mandatory operations successful and AppliedGeneration equal to that generation.
No latest selection, current alias lookup, external Provider call or provisioning
occurs during rendering.

Consume confirmed outputs from the containing accepted graph. Only explicitly
bound Resources with Document projections may contribute fragments. Initially
support local-resource bindings; exports and cross-Application consumption remain
later capabilities. PostgreSQL connection rendering uses the final confirmed
application-login output, not a reconstruction from mutable Provider state.

The ConfigurationDocumentRevision is an immutable, Provider-neutral artifact
bound to the exact generation, Component/document and frozen rendering inputs:
source revision, output associations, selected profile revisions, effective
options, exact renderer/formatter contracts and serializer profile. Store the
resulting semantic tree with unresolved references/deferred values. Identical
inputs return the existing artifact; never overwrite an accepted artifact.
Versioned implementation compatibility must preserve the registered semantics;
incompatible behavior requires a new exact contract/profile identity.

Materialization is an explicit operation separate from provisioning. It does not
increment DesiredGeneration, alter AppliedGeneration or retroactively add another
mandatory node to the accepted provisioning graph. Changed planning inputs require
new accepted effective desired state; materialization cannot silently switch a
profile or options within an existing generation.

### First supported document pipeline

Use existing Component documents and Binding Document projections. The first
executable format is JSON, using the ADR-017 immutable format-neutral tree and
object-only canonical JSON Pointer insertion. Keep all-or-nothing deterministic
composition and collision refusal; no enumeration-order precedence, recursive
fragment merge or overwrite policy.

Start with static document templates and projections. Liquid and parameter
execution follow separately under ADR-017/019. Until supported, a requested
expression/evaluation must refuse explicitly, never silently become literal
application configuration. YAML and raw text remain valid declaration formats
but unsupported execution returns a typed refusal until their serializers are
implemented. This does not introduce confidential raw-text/Liquid interpolation.

### Typed deferred formatting

Extend the internal ConfigurationValue model with ConfigurationDeferredValue,
a typed deferred-formatting node. It contains an exact versioned formatter
contract and validated typed inputs, including exact SecretReference nodes where
required. It is not a magic user JSON object, arbitrary expression, executable
script, CLR type name or already serialized secret-bearing string.

Renderers remain pure: typed Resource outputs plus validated effective consumer
inputs produce a fragment. A fragment can now contain DeferredValue as well as
the existing node alternatives. Renderers do not read SecretStore, call Providers,
receive the whole document for mutation or resolve plaintext. Liquid cannot
construct or evaluate deferred values or obtain SecretRefs/plaintext.

A narrowly registered formatter owns the typed formatting semantics. For a
PostgreSQL connection string, the PostgreSQL adapter owns its format, parameter
mapping and correct escaping. Common contracts and storage do not add host,
role, timeout, connection-string or PostgreSQL-specific fields.

Only an explicitly authorized delivery operation resolves the required exact
SecretRefs transiently and invokes the formatter, then serializes the completed
document. Ordinary serializers must refuse unresolved secret/deferred nodes;
preview uses a separate redacted representation. Missing/ambiguous/incompatible
formatter registration or invalid inputs fail closed without partial delivery.
A formatter cannot request unrelated secrets or perform external provisioning.
Its validated input references and the delivery scope bound secret resolution.

Treat the complete deferred formatted value as confidential, including in
preview, diagnostics, tracing and logs. Persist neither materialized connection
strings, plaintext passwords, verifiers nor material-derived fingerprints.
Fingerprints/equality may include safe typed inputs and exact SecretRef identities,
not secret contents. Keep secret buffers bounded/disposable; acknowledge managed
string lifetime where a library necessarily requires strings.

### Binding.options describes consumption

Binding.options carries typed consumer-specific settings, separate from Resource
properties and provisioning. Two Components can bind one login/SecretRef while
choosing different connection timeout values; this must not create another role,
rotate a password or change Resource ownership/Provider selection.

Use the exact Resource/output binding-options schema under ADR-018 and typed
adapter codecs; no arbitrary connection-string suffix, unvalidated dictionary
or secret interpolation. Allowed parameter names, types, units, bounds, defaults
and security restrictions are specified by the relevant Provider/adapter contract.
[ADR-040](0040-postgresql-connection-rendering-and-consumer-options.md) now selects
the first PostgreSQL allow-list: timeout, commandTimeout and sslMode, with a
verify-full renderer default. These are adapter-specific rules, not a universal
Provider policy. Connection and command timeouts are distinct.

Effective settings have explicit precedence:

1. Documented renderer defaults.
2. Defaults from the exact Configuration Profile revision.
3. Explicit Component Binding.options.

Validate the effective typed input and applicable security constraints after
resolution. This precedence covers only declared consumer options; it does not
authorize replacing output-derived endpoint/database/user/SecretRef, changing
privileges, or weakening a security restriction. TLS options, if exposed, need
explicit adapter policy; the isolated PostgreSQL pilot is not a universal
SSL-disabled deployment recommendation.

### Minimal reusable Configuration Profiles

[ADR-037](0037-environment-configuration-profiles-and-scoped-selection.md)
refines this section: Environment defaults select Profiles automatically, with
whole-Profile revision selection/disable/add decisions at Application and Component
scope. It replaces mandatory individual assignment/mapping with author-declared
Binding slots. Document definitions are separate from static and Binding-driven
contributions, allowing independent Profiles to contribute to one document.

A Configuration Profile is a separate reusable immutable configuration-layout
entity, not a Resource privilegePolicy or Kubernetes Workload profile. Select it
with ProfileRevisionRef { profileId, revision }; no unpinned latest reference.

The minimal profile supplies:

- structured document definitions/templates and artifact names;
- explicit logical Binding slots;
- Document projection mappings for those slots, paths and exact renderer IDs;
- declared typed renderer/consumer defaults.

For example, a reusable layout can supply settings JSON with
PostgreSQL.DefaultConnection and project a connection-string fragment into its
ConnectionString member. The profile's storage slot matches an explicit slot on
the Component's declared storage Binding, which targets its own app-login.
The profile never guesses Resource names, creates a Binding to arbitrary Resources
or grants access merely through profile membership.

Expand the selected Profiles and effective scope decisions into validated effective
documents/projections before renderer execution. Validate binding-slot resolution,
exact contracts, local references, effective options and the complete projection
path set. Duplicate/conflicting document definitions or projections fail closed;
there is no implicit manifest-vs-profile structural merge or last-writer rule.
The profile and Application retain independent immutable revisions; profile
expansion does not rewrite the imported ManifestRevision.

Pin profile revision and effective assignment/options with the planning inputs.
Missing profile/slot/contract or incompatible options refuse acceptance. Materialize
only from those frozen inputs. Record document/fragment provenance separately
from values, including profile revision, source document, Binding and renderer.

Creating profile revision 2 does not change consumers pinned to revision 1.
A rollout requires explicit new accepted effective desired state through the
planning model. The current generation-1 pilot cannot promise updating all
existing deployments; do not bypass that boundary by silently re-rendering them.
Initial creation with exact profiles precedes later rollout/update support.

Environment defaults and per-ID scope selection are accepted under ADR-037.
Broad selectors, overlapping target sets, group targeting, JSON Merge Patch/JSON
Patch, arbitrary structural overrides, Profile parameters and mass rollout remain
deferred. Do not implement the full planned ADR-011 profile platform.
The exact minimal Profile/selection wire schema, bounds and lifecycle operations
must be specified before implementing their transport/storage boundary; examples
in discussion are conceptual, not newly valid Application JSON members.

### Persistence, preview and confidential delivery

Follow ADR-035: shared generic profile/assignment/document-revision envelopes,
exact identities and versioned typed payloads, not Provider-specific tables or
columns, EAV or a single universal table. Keep actual secret material exclusively
within SecretStore under ADR-006. Use source-generated JSON metadata and explicit
build-time registrations; no reflection discovery or serializer fallback.

Operator preview substitutes a redacted marker for each secret or complete
confidential DeferredValue and returns canonical redacted paths. It is not an
application-ready document. Ordinary Plan/get/list/CLI JSON and logs must not
materialize confidential values. A preview is not the persisted semantic tree.

Confidential retrieval is a separate API boundary. Before enabling it, accept
exact document-revision selection, minimal authorization/scope, transport and
secret-bearing response handling. Deferring Kubernetes Runtime Identity/general
operator auth never permits unrestricted configuration retrieval. No reveal
endpoint is authorized by this ADR.

## Required evidence

### Accepted first composition slice

The [first composition contract](../architecture/configuration-composition-v1.md)
fixes ConfigurationFormatterId, immutable Arguments, registration/validation,
all-or-nothing object projection, ordinary JSON serialization refusal and the
separate redacted preview envelope. Arguments may contain exact SecretReference
nodes but no nested ConfigurationDeferredValue. The formatter initially promises
a String result; this slice validates its contract without executing formatting.

Implement the pure composition boundary with supplied static templates and ready
fragments first. This does not yet resolve bindings, load outputs, expand profiles,
persist document revisions or expose Host/CLI endpoints. Those subsequent slices
reuse this boundary. ADR-037 accepts the subsequent typed in-memory Profile
selection/expansion slice; its wire contract must still be agreed before wire
implementation. Confidential delivery remains gated.

- Two Components bind one login: identical exact SecretRef, different permitted
  consumer options, deterministic separate documents without new credentials.
- Reusable profile supplies layout/projections without per-Application copying;
  slot mismatch, profile/manifest collision and missing exact registration refuse.
- Unresolved typed tree survives persistence/restart with exact profile/output
  references; repeated materialization is idempotent and failed composition
  creates no partial document revision.
- Preview contains neither password nor completed confidential formatted value;
  ordinary serialization cannot materialize deferred nodes.
- Authorized formatter tests later prove correct escaping and real client use,
  without making test authorization an unrestricted production delivery route.
- A typed non-PostgreSQL example exercises changed shared formatting/profile
  boundaries; no claim of a second production Provider follows from that test.

## Consequences and delivery sequence

Implement narrow vertical slices: finalize minimal profile/options/formatter
contracts, compose and persist unresolved JSON documents, expose redacted preview,
then separately authorize confidential delivery and prove a client can retrieve
configuration and connect. Preserve current provisioning/retry/uncertainty and
single-Application ChangeSet behavior throughout.

ADR-033 permits explicit fresh current-format catalogs for breaking slices,
never automatic reset. This ADR changes architectural contracts only; it does
not claim rendering, profiles, formatting or delivery are already implemented.
