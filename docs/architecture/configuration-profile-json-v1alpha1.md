# Profile and assignment JSON contracts

Implemented for issue #128 under [ADR-038](../adr/0038-profile-json-contracts-and-explicit-secret-projections.md).
`ProfilePayloadParser.ParseProfile` and `ParseAssignment` in Haby.Manifest accept
standalone UTF-8 bytes and return immutable Domain declarations. This is an
in-process wire boundary, without storage, apply, Host/CLI commands, Environment
lookup, authorization, output access, secret reads or formatter execution.
[Issue #132](../development/132-profile-revision-persistence.md) adds separate
side-effect-free import/exact-read use cases and immutable PostgreSQL persistence
over this parser. [Issue #134](../development/134-profile-operator-path.md) exposes Profile administration
through the operator Host and habyctl. [Issue #136](../development/136-scoped-profile-assignments.md)
adds persisted assignment administration; planning and delivery remain deferred.
The Profile wire boundary itself requires no catalog reset. Issue #130 now adds
Application Binding `configurationSlot` to the current v1alpha1 and advances its
normalized/snapshot versions to 2, requiring an explicit fresh catalog for
superseded snapshots. See [reset requirements](../development/130-binding-configuration-slot.md).

## Schemas and DTOs

| Root | Checked-in Draft 2020-12 schema | Canonical ID |
| --- | --- | --- |
| ConfigurationProfile | `schemas/configuration-profile.v1alpha1.json` | `https://haby.dev/schemas/configuration-profile.v1alpha1.json` |
| ConfigurationProfileAssignment | `schemas/configuration-profile-assignment.v1alpha1.json` | `https://haby.dev/schemas/configuration-profile-assignment.v1alpha1.json` |

Both roots require exact `apiVersion: haby.dev/v1alpha1`, exact `kind` and `spec`.
All contract objects are closed; arbitrary ordinary JSON is permitted only in
`template`, contribution `value`, and rule `options`. There is no `$schema`,
Environment field, UUID or latest alternative. Profile additionally requires
`metadata: { id, revision }`. Assignment has no metadata. Profile IDs use existing
ApplicationId rules; declaration map keys/references use manifest-local ID rules.
Revisions and named-secret versions are positive Int32 values. Exact mathematical
integer spellings such as `1e0` normalize to 1 without floating-point conversion.

`ConfigurationProfileDocument` and `ConfigurationProfileAssignmentDocument` are
DTO roots in the metadata-only `ManifestJsonContext`. Assignment targets and
Use/Disable decisions use explicit source-generated `kind` discriminators,
including when the discriminator appears after other members. DTOs alone are
not an accepted declaration: use the parser to enforce schema and semantic rules.

Profile spec optionally contains `documents`, `staticContributions`, `bindingRules`.
Omission and empty maps normalize identically; null refuses. Document definitions
require fileName/format/template, with JSON as the only format. Static contributions
require documentRef/path/value; null is a valid ordinary value. Rules require
slot/rendererRef/documentProjections. Options omitted or `{}` normalize to an
empty Object; null refuses. A projection requires documentRef/path. Projection
lists are nonempty sets: duplicate entries refuse and order is insignificant.
No document defined in another selected Profile must exist during parsing.
Effective document, slot, filename and outer-path collisions remain expansion checks.

There are at most 32 documents, 32 rules, and 256 combined static contributions,
Binding document projections and secret projections. Each contribution counts
once, plus once per secret target. Each individual collection is bounded by its
possible aggregate maximum. Input is limited to 1 MiB and 64 JSON container levels.
The reused raw scanner rejects duplicate members at every level (including ordinary
values), comments, trailing commas/content, malformed Unicode/UTF-8, BOM and
non-JSON numbers before deserialization. Ordinary numeric nodes retain arbitrary
precision through the existing ConfigurationValueMapper; no double/decimal coercion.

Assignment spec requires a closed target and 0-64 decisions. Targets are
`{ kind: environment }`, `{ kind: application, applicationId }`, or
`{ kind: component, applicationId, componentRef }`. Decisions are
`{ kind: use, profileRef: { profileId, revision } }` or
`{ kind: disable, profileId }`. Duplicate logical Profile IDs refuse across both
variants. Empty decisions inherit; ordering is insignificant. The immutable target
is retained separately from decisions. The trusted operation must supply the
Environment later. No assignment is applied or authorized by parsing.

## Ordinary example and composition

Use the complete [layout](../examples/configuration-profiles/layout.json),
[Serilog](../examples/configuration-profiles/serilog.json),
[connections](../examples/configuration-profiles/connections.json) and
[Environment assignment](../examples/configuration-profiles/environment-assignment.json)
examples together. Layout owns settings/appsettings.json, Serilog contributes its
whole subtree, and connections declares two opaque typed synthetic renderers.
A Component explicitly associated with slot main consumes its existing Binding;
a Component using events receives the other rule. No Binding is inferred or created.
The [Application example](../examples/application-with-configuration-slots.json) declares
this association with Binding `configurationSlot` for api and worker. Its Resource
type is synthetic; importing it requires matching test/downstream contract metadata.

Parse each Profile into ConfigurationProfileRevision, construct the exact
ConfigurationProfileCatalog, then pass parsed assignment decisions to
ConfigurationProfileExpander at the matching scope. Its effective layout supplies
ordinary static contributions and unresolved exact render requests to the existing
composer seam. Tests provide two synthetic typed output families, preserving their
exact credential reference, with different consumer settings. The composer and
redacted preview exercise those fragments; no second production Provider or actual
renderer/formatter execution is claimed. Environment -> Application -> Component
selection replaces a whole Profile revision, as documented in the
[expansion contract](configuration-profiles-v1.md).

## Explicit secret projection declarations

The [telemetry example](../examples/configuration-profiles/secret-projection.json)
parses independently of any Environment or SecretStore. Each optional
`secretProjections` item requires relative path, exact format `string` or `json`,
and a closed `secretRef: { name, version }` using existing named-secret rules.
Omission/[] means none; null refuses. Names and versions remain declarations,
not resolved internal SecretRefs. No material or secret JSON is read or parsed.

The immutable ProfileStaticContribution retains typed ProfileSecretProjection
items. Path/name/version/format all participate in structural equality/hash and
same-revision catalog conflict detection; list order does not. Collections are
snapshots. Relative paths use canonical non-root JSON Pointers: `/` is the empty
Object key; escaped/numeric keys work. Missing Object parents are allowed.
Non-Object roots/parents, occupied leaves, duplicate and ancestor/descendant targets
refuse. Each secret target reserves an opaque whole node. The outer contribution
reserves its entire subtree during selected-document collision checks.

Current expansion explicitly refuses any effective secret-bearing contribution
with `profile.secret-projections.unsupported`, returning no layouts, including for
direct semantic Component contributions. It cannot produce an ordinary document
with the requested insertion silently omitted. Callers must use this expansion
gate before composing Profile contributions; composing a bare Value would discard
declaration metadata and is not Profile materialization. Disabling or replacing a
secret-bearing Profile before expansion uses only the actual effective revision.

Ordinary magic-looking JSON remains ordinary: no discriminator/member can create
SecretReference, DeferredValue or commands. Future planning must pin exact internal
references. [ADR-039](../adr/0039-scoped-profile-assignment-administration.md)
accepts trusted administrator assignment as distribution consent for all declared
secrets in the target scope; no per-secret permission check is required in this
pilot. Confidential retrieval still requires its separate authorization boundary.
Future delivery must implement typed JSON insertion, whole-node redaction and
all-or-nothing output. A String-only formatter is insufficient for format json.
None of those capabilities or permissions is provided by successful parsing.

## Normalized exact declaration reads

Under ADR-039 and issue #138, successful exact Profile get returns the full current
portable ConfigurationProfile object in CLI JSON `data.declaration`, with exact
identity and original audit beside it. The Host rehydrates the accepted immutable
revision through the existing query/store and explicitly maps to the DTO roots in
ManifestJsonContext. Generated protobuf carries portable UTF-8 JSON bytes in
`ProfileReply.declaration_json`; this field is absent on all other outcomes and
commands. The CLI embeds a checked JsonElement as an object through generated
metadata, never a JSON string or Google Struct/double representation.

Maps and set-like projections are ordered ordinally, defaults may be explicit,
and exact numbers and all ordinary JSON kinds survive. Original bytes/source
formatting and internal snapshot encodings are not returned. The CLI validates
root/kind/version, structural completeness, exact identity and audit/outcome
consistency before output, without duplicating semantic acceptance. Empty spec
maps, empty options and empty secretProjections use documented omitted defaults.
Portable numeric spelling may differ from internal CanonicalText: zero is `0`,
unnecessary `e0` is omitted and a shorter exact plain form may be used; huge
exponents remain scientific. The same compact policy covers positive Int32
revision/secret-version fields (for example `1e9`), with exact bounded-integer
reply validation. No double/decimal conversion, decimal expansion,
Domain equality/hash/normalization, snapshot or existing configuration JSON change
is involved. Portable JSON uses minimal lossless string escaping, including
Unicode in ordinary keys and contract paths; the CLI embeds the checked tokens
without re-escaping. This is output normalization, not a digest/canonicalization
standard. Existing parser/CLI/transport admission limits remain unchanged. Text remains
a short summary. Named-secret selectors are declarations only and are returned
even when missing; no secret resolution, rendering, output access or material
lookup occurs. Ordinary credentials embedded by mistake are returned as stored.
Validate/import/assignment/error responses still contain no Profile content.
See the [operator guide](../development/134-profile-operator-path.md) for extraction,
reimport, boundary tests, safe failures and exit conventions. No storage model,
migration/reset, network/auth policy or planning/delivery capability is added.

## Diagnostics

ProfileWireResult exposes IsSuccess, Declaration, Diagnostics, ErrorCount and
IsTruncated. Every diagnostic is an error with only Code and canonical source
JSON Pointer Path (root is empty). Values, Arguments and exception messages never
appear. Sort ordinally by Path then Code, deduplicate pairs, and return the first
100 distinct diagnostics; ErrorCount counts distinct findings before this result
limit. Schema evaluation uses the existing bounded collector (20,000 events);
invalid schema status always refuses even if collector detail is exhausted.
Any error yields Declaration = null; no truncation can hide failure.

| Code | Meaning |
| --- | --- |
| profile.input.too-large | Exceeds 1 MiB. |
| profile.syntax.invalid-encoding | Invalid UTF-8 or UTF-8 BOM. |
| profile.syntax.invalid-json | Invalid syntax, Unicode, or trailing content. |
| profile.syntax.duplicate-property | Duplicate decoded member name. |
| profile.syntax.max-depth-exceeded | Exceeds 64 container levels. |
| profile.schema.constraint-violation | Closed wire contract/schema/DTO refusal. |
| profile.declaration.invalid | Immutable declaration invariant refuses. |
| profile.declaration.limit-exceeded | Combined contribution/projection count exceeds 256. |
| profile.secret-projections.invalid | Relative insertion conflict or invalid declaration. |
| profile.projection.duplicate | Duplicate rule projection after mapping. |
| profile.selection.duplicate | Multiple decisions for one ProfileId. |

Wire validity, immutable semantic validity and executable availability are separate.
These results do not reuse Application-import success or claim Profile persistence.
