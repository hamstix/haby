# ADR-038: Profile JSON contracts and explicit secret projections

- Status: Accepted
- Date: 2026-11-05
- Scope: P2 Profile/assignment wire contracts and the current alpha Binding slot
- Extends: ADR-006/017/020/028/029/033/036/037

## Decision

### One current alpha Application contract

During active development under ADR-033, agreed changes may modify the current
haby.dev/v1alpha1 schema, source-generated DTOs and normalized model together,
without changing apiVersion. Do not retain earlier alpha adapters/schemas solely
for intermediate-build compatibility. This supersedes ADR-020's former requirement
that every published alpha input version remain immutable while supported.

Before supported compatibility begins, explicitly declare the API/schema baseline
and its evolution/upgrade policy. A slice changing the saved model must update
the snapshot codec/model version coherently and document a fresh-catalog requirement
if needed; no automatic reset or silent loss of fields. Within a current build,
ApplicationId + ApplicationVersion still identifies one immutable ManifestRevision.
Changing the alpha contract never authorizes replacing an accepted revision.

### ConfigurationProfile

The standalone JSON root requires apiVersion = haby.dev/v1alpha1,
kind = ConfigurationProfile, metadata and spec. metadata requires id (the existing
ProfileId lexical rules) and positive Int32 revision. It contains no Environment,
Provider instance, server record ID or latest reference. Identity is id + revision;
equivalent normalized content is an idempotent repeat and differing content conflicts.

spec contains optional documents, staticContributions and bindingRules maps.
Omission/{} normalizes to an empty map; explicit null is invalid. Map keys are
manifest-local IDs in independent namespaces. Object enumeration order is not
semantic. An empty Profile is valid.

documents use the existing fileName/format/template shape. The first Profile wire
contract supports static JSON documents only. Document identity/file-name ownership
and cross-Profile references follow ADR-037. Profile loading validates its portable
declaration, not the existence of Environment-specific Bindings or the entire
selected Profile set. Cross-Profile document resolution belongs to expansion.

A static contribution requires documentRef, path and value. value is arbitrary
ordinary JSON, including null. Its path is a canonical non-empty JSON Pointer
into the destination document. The contribution may additionally declare
secretProjections as specified below.

A bindingRules entry requires slot, exact rendererRef and non-empty
documentProjections. options is an optional JSON object, absent/{} -> empty;
explicit null is invalid. Each document projection requires documentRef and path.
Projection lists have set semantics: duplicates refuse, source order does not
affect normalization. Rules, options and output interpretation remain Provider-neutral.

Initial bounds: at most 32 document definitions, 32 Binding rules and 256 combined
static contributions plus Binding document projections per Profile. Count each
secret projection toward that same total bound as well. Existing strict
intake/depth/transport-size boundaries also apply. Concrete secret payload and
final materialized document limits must be specified before delivery is implemented;
this wire-only slice does not read secret payloads.

### Explicit static secret projections

Profiles can refer to named secrets without declaring a Resource, Provider adapter
or Component Binding. A contribution may contain:

```json
{
  "documentRef": "settings",
  "path": "/OpenTelemetry",
  "value": {
    "EnableTracing": true,
    "EnableMetrics": true,
    "EnablePrometheusEndpoint": true
  },
  "secretProjections": [
    {
      "path": "/Otlp",
      "format": "json",
      "secretRef": { "name": "otel-connection", "version": 1 }
    }
  ]
}
```

secretProjections absent/[] means none; explicit null is invalid. Every item
requires path, format and secretRef; all objects are closed. path is a canonical
non-empty JSON Pointer relative to value. Apply the existing Object-only insertion
rules: create missing parents, refuse occupied leaves, equal/overlapping paths and
non-Object traversal/root. The final whole contribution occupies its outer path;
another contribution/projection cannot write inside that occupied subtree.

format is explicitly string or json. string inserts the secret text as a String.
json parses one strict JSON value and preserves its type and exact numbers. Reject
duplicates, comments, trailing content, malformed encoding/JSON and exceeded size
or depth limits. Never copy rejected secret text into errors or parser diagnostics.
Objects in value or secret JSON never become SecretRef, DeferredValue or commands;
there is no recursive secret resolution or expression execution.

The operator-facing secretRef contains an existing named-secret identity and an
explicit positive operator version number. Profile parsing/loading performs no
secret read and does not require the name to exist in any particular Environment.
Planning resolves it within the current Environment to the exact internal
SecretRef and fixes that identity plus format in planning inputs. No latest lookup
or name re-resolution during delivery.

Until authorized delivery, persist only unresolved exact reference/format nodes
and safe structural metadata, not secret material or parsed secret JSON in a
ConfigurationDocumentRevision. Delivery resolves the exact secret, parses json
when requested and substitutes it transiently. The whole inserted node is
confidential for both formats and any resulting JSON type. Preview redacts it
wholly and returns its final canonical path; ordinary serialization refuses it.
No format-json node may be coerced through a String-only formatter contract.
The internal representation must retain reference and format as typed information.

A failed resolution, decode, parse or insertion refuses the entire document
delivery, with no partial secret-bearing response. A secret JSON root Object/Array
is not permission for other projections to inspect or insert into it.

Assigning a secret-bearing Profile grants distribution to Components receiving
that effective Profile. Under [ADR-039](0039-scoped-profile-assignment-administration.md),
the isolated operator API trusts its administrator: assignment itself authorizes
all declared secrets for that scope, including future inheriting Components.
No separate per-secret operator permission check is required in this pilot.
Assignment does not resolve names or read material. Component configuration
retrieval still requires a separately accepted authorization boundary. Pure
parsing/expansion tests do not establish an implemented delivery security gate.

### ConfigurationProfileAssignment

The standalone closed root requires apiVersion = haby.dev/v1alpha1,
kind = ConfigurationProfileAssignment and spec. There is no arbitrary metadata.id;
the logical identity is the Environment context plus the exact target scope.
The Environment comes from the trusted API/CLI operation context, not this document.

spec requires target and decisions. target is an explicit source-generated union:

- { "kind": "environment" }
- { "kind": "application", "applicationId": "processor-service" }
- { "kind": "component", "applicationId": "processor-service", "componentRef": "api" }

No selectors, groups, alternate-target members or UUID guessing. A decision is:

- { "kind": "use", "profileRef": { "profileId": "serilog-defaults", "revision": 4 } }
- { "kind": "disable", "profileId": "serilog-defaults" }

decisions is required, non-null, at most 64 entries and may be empty. Empty means
no local decisions, inheriting the higher scope. Duplicate logical ProfileIds
refuse even if decisions are identical; list order is not precedence. Whole-Profile
selection remains Environment -> Application -> Component under ADR-037.

Future assignment apply replaces the complete local decision set under optimistic
concurrency. Concurrency metadata belongs to the operation, not the portable JSON.
Changing assignment never directly mutates an accepted Plan/generation or triggers
automatic rollout. Structural parsing does not claim scope/secret authorization.
[ADR-039](0039-scoped-profile-assignment-administration.md) defines persisted
existence checks, explicit Component validation version, conditional replacement,
trusted-administrator consent and the operator administration boundary.

### Application Binding configurationSlot

Add optional configurationSlot directly to a Binding in the current v1alpha1:

```json
{
  "target": { "kind": "local-resource", "resourceRef": "app-login" },
  "configurationSlot": "main-database",
  "options": {}
}
```

Absent means no requested Profile slot; null/empty/invalid local IDs refuse. One
slot per Binding, at most one requesting Binding per slot in a Component. Existing
direct documentProjections remain available and share complete collision checks
with emitted Profile contributions. The slot never infers a Provider or grants
Resource access. Preserve it in normalization, equality, diff, snapshot round-trip
and exact future planning inputs. Do not publish an alpha2 solely for this change.

### Strict wire handling and delivery sequence

Use explicit source-generated DTO roots/discriminators, closed objects, duplicate
member rejection, strict UTF-8/JSON intake and exact numbers. Ordinary values never
serve as magic internal-node serialization. Profile/assignment validation uses
safe standard diagnostics without source values or raw exception text.

The next slice implements Profile/assignment schema, DTO, validation and mapping
to immutable semantic declarations, with in-process selection/expansion/composition
tests for the supported nonsecret path. Secret projection declarations must retain
their name/version/format/path as immutable typed data and participate in collision
validation/equality; never drop them when mapping to the earlier ordinary-only model.
Until exact-reference planning and authorized delivery are implemented, attempting
to turn a secret-bearing declaration into deliverable configuration must refuse
explicitly rather than emit a document with the secret path silently omitted.

The following slice updates Application v1alpha1 slot parsing, normalized/snapshot
models and Import coherently. No old-alpha preservation layer is required. Profile
persistence, assignment APIs/authorization, exact secret planning, real renderers,
materialization and delivery remain subsequent work. Schema acceptance is not
permission to expose a secret endpoint or bypass the selected deployment scope.

## Required evidence

- Profile/assignment schema and DTO/semantic validation agree on defaults,
  closed discriminators, map/list limits and exact immutable identities.
- Scope/secret reference resolution is not attempted while parsing a portable Profile.
- Ordinary Profile and assignment JSON reaches the existing expansion/composer/preview
  seam with whole-Profile replacement, explicit slots and second-family tests.
- Secret projection metadata survives mapping/equality; relative/outer collisions
  refuse and unsupported execution cannot silently omit the requested secret.
- Future delivery tests must cover String and every JSON root type, exact numbers,
  strict invalid JSON, limits, no nested interpretation, whole-value redaction,
  authorization and no partial output/material persistence on failure.
- The later slot slice proves current schema/DTO/normalization/snapshot equality
  and round-trip without an alpha2 adapter or silent legacy fallback/reset.
