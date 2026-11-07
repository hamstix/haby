# ADR-041: Configuration document materialization and redacted preview

- Status: Accepted
- Date: 2026-11-07
- Scope: P2 immutable unresolved documents and the operator materialize/get/preview path
- Extends: ADR-017/026/027/033/035/036/037/039/040

## Decision

### Operator selectors and exact internal targets

Internal operations use the exact ApplicationDeploymentId, generation,
ComponentId and DocumentId. The operator supplies Environment name, portable
ApplicationId, ComponentId and DocumentId. ApplicationId is the existing portable
Application identity, not a catalog UUID. Environment selection reuses configured
CLI context and explicit --environment override; registry selection is unchanged.

An optional positive --generation selects that exact generation inside the named
Environment/Application deployment. Without it, the server resolves the current
desired generation only when DesiredGeneration == AppliedGeneration and all
mandatory operations are successful. Refuse pending/uncertain/partial current
desired state; never fall back to the previous applied generation.

Resolve the selector once, server-side, from a consistent view. Thereafter use
only the exact target. A concurrent deployment change may make conditions fail,
but must not retarget the request or restart current-generation selection. Return
the actual selected generation and exact identities with the result. This is a
resolved-target result, not a promise that it is still current when received.

Component/document existence is checked against that generation's frozen layout,
not a latest manifest, mutable Profile assignment or last validation revision.
Exact existing historical reads with --generation need no currently available
renderer or repeated provisioning. Omitted generation still follows the strict
current desired/applied rule even for get/preview.

### Materialization gate and pipeline

Creation requires AppliedGeneration equal to the exact requested generation and
all mandatory operations successful. This gate applies even to a static-only
document. Pending, Dispatching, NeedsAttention, partial success or wrong evidence
refuses safely. No external provisioning, alias resolution or current Profile
selection occurs. Reads of existing immutable artifacts are separate from
eligibility to create a new artifact.

Materialize one document atomically:

```text
exact frozen layout and render pins
  -> confirmed containing-generation outputs
  -> pure renderers
  -> unresolved fragments
  -> existing deterministic composer
  -> immutable unresolved semantic tree
  -> bounded validation and persistent acceptance
```

Use saved document template, static contributions, Profile revisions, Binding
requests/projections, options/defaults/provenance and exact capabilities from the
accepted generation. Verify output Resource/Environment/Application/generation,
producer/attempt/action, contract and key through the confirmed-evidence boundary.
No reconstruction from mutable Provider configuration or current aliases.

Only contributions targeting the selected document participate in composition.
A Binding's exact fragment may serve its multiple explicit projections; neither
enumeration order nor duplicate execution chooses a winner. Preserve Object-only
pointer insertion, missing parents, collision refusal and all-or-nothing output.

The formatter is not invoked. Do not resolve/read/check secret material or call
external Provider APIs. Static JSON/local Bindings are the initial capability;
retain explicit unsupported gates for direct Profile secret projections, Liquid,
parameters and unsupported document formats. Never silently omit a requested value.

### Immutable artifact identity and idempotency

One ConfigurationDocumentRevision is uniquely keyed by deployment + generation +
Component + Document. The server assigns a ConfigurationDocumentRevisionId.
No additional sequential document revision number is needed in this first model.
fileName and format are artifact attributes, not identity.

Created accepts a complete artifact; Existing returns the same stored identity,
tree, inputs, provenance and original audit. A repeat with an already saved exact
artifact does not re-execute renderers or re-read outputs. Concurrent requests
converge on one complete winner and return that artifact after checking equivalent
exact inputs. An incompatible artifact under the same key is an explicit integrity
conflict, never overwrite. New inputs require a new desired generation.

One document is one transaction. Failure leaves no partial revision. Documents
for api and worker use separate requests/transactions; no all-Application batch
transaction is introduced. Materialization changes neither DesiredGeneration,
AppliedGeneration, operation graph/state, output evidence nor credentials.

Persist asynchronously with the current EF Core/C# workflow and declarative
constraints under ADR-026. No new stored workflow functions/triggers or mandatory
database role split. Cancellation before commit can leave no record; confirmed
commit is not discarded by a late cancellation check. A lost response does not
prove rollback. Repeating an exact target is safe; no automatic retry is added.

### Stored representation and fingerprints

Store exact artifact identity; fileName/format; source ManifestRevision and Plan;
frozen input fingerprint; consumed output associations; Profile/render/formatter
and implementation identities; serializer profile; semantic unresolved tree;
provenance; original trusted actor and UTC creation time. Reuse exact immutable
source references without pretending that mutable environment settings are sources.

SecretReference and DeferredValue remain typed nodes. A closed versioned internal
codec preserves ordinary JSON kinds, arrays, ordinal objects and exact numbers.
Internal node decoding is limited to the trusted storage envelope. An ordinary
template Object such as {"kind":"SecretReference"} stays an ordinary Object,
including inside a deferred argument's ordinary subtrees. No magic JSON parsing.

Use a deterministic versioned persistent fingerprint of exact inputs, unresolved
tree and declared format/serializer identities, never process GetHashCode().
SecretRef identity may participate; secret bytes, verifiers and formatted strings
must not be read, persisted or hashed. This is integrity/consistency metadata,
not a security promise against privileged catalog editing.

Shared persistence contains generic document fields/versioned payloads under
ADR-035; no PostgreSQL connection columns or Provider-specific document tables.
Use explicit source-generated metadata or closed writer/reader codecs, not CLR
reflection discovery/serialization. Strict corrupt/unsupported reads fail safely,
never return empty/NotFound success, repair state or rebuild from current defaults.

### Redacted preview and safe reads

Get returns safe artifact metadata and original audit, without unresolved tree,
formatter arguments, secret selectors or material. Preview reads the saved tree
and emits the existing separate document/redactedPaths representation. It is
read-only and never implicitly materializes an absent artifact.

Replace every complete SecretReference or DeferredValue with ***REDACTED*** and
record the final canonical path, sorted/deduplicated. Do not expose deferred
arguments or exact secret identities in preview. Ordinary string values equal to
the marker do not create redactedPaths. Root confidential values use the existing
empty root pointer convention. Ordinary serialization continues to refuse
unresolved confidential nodes.

Ordinary static values remain visible. Credentials mistakenly embedded directly
in an ordinary template are returned as ordinary values, as with Profile full
read. This is typed confidentiality, not heuristic password-field detection.
Preview is not an application-ready configuration or an authorized delivery.
Neither saved preview nor get requires active adapter registration, SecretStore
or external database availability. Generic codec/serializer support is still
required and unavailable formats refuse visibly.

### Formats, limits and operator commands

The first serializer profile is haby.configuration-json/v1, pinned in the artifact.
Do not silently change its escaping or numeric policy. Persist semantic content,
not appsettings.json bytes or a completed secret-bearing connection string.

Before acceptance enforce final tree depth <= 64 containers, complete internal
revision payload <= 4 MiB UTF-8 and complete redacted preview envelope <= 4 MiB
UTF-8. Both encoded-size checks must pass before save, so an accepted revision is
previewable under this contract. Bound composition/encoding work and refuse
without partial records/output on excess. Internal wrappers may need a separate
explicit codec depth budget; they do not reduce the allowed semantic tree depth.

Use a bounded 8 MiB configuration response budget, accounting for protobuf and
the outer CLI envelope around the already bounded preview. Do not enlarge import
request/file limits. Test actual UTF-8/protobuf/envelope sizes and depth at the
boundary; preview is an object in JSON output, not a doubly escaped JSON String.

```text
habyctl configuration materialize processor-service --environment production --component api --document settings
habyctl configuration get processor-service --environment production --component api --document settings
habyctl configuration preview processor-service --environment production --component api --document settings --output json
```

Each command supports optional --generation. configuration is distinct from the
existing local config group. No UUID is required in normal operator calls. JSON
includes exact DeploymentId/ConfigurationDocumentRevisionId and selected generation;
text identifies Environment/Application/generation/Component/document and outcome.
Get/preview of an absent artifact returns NotFound and performs no write. No
additional UUID command is necessary. Text preview may include readable redacted
JSON after a summary; JSON remains one standard result envelope.

Keep expected refusals distinct from corrupt/infrastructure failures and sanitize
diagnostics/logs. Validate reply identity/shape before output. CLI remains API-only
and production Native AOT; generated gRPC/JSON contracts own the transport.

Idempotency is defined for the exact resolved target. A new request without
--generation can select a newly applied generation. Therefore do not automatically
retry current selectors after uncertain responses. Show the resolved target where
available and explain explicit-generation retry/inspection for automation.
Other commands may later gain name-based convenience, but are unchanged by this ADR.

## Required evidence and scope

Real generated client/Host/PostgreSQL/published CLI: five-step execution, api/worker
materialization with one SecretRef and distinct timeouts, complete redacted preview,
equivalent/concurrent repeat, no state mutation, restart and real dump/restore.
Prove current desired/applied selection, no fallback or mid-request retargeting,
explicit historical reads and exact retry behavior. Test incomplete/uncertain
generation, missing/wrong outputs, collisions, unsupported inputs and exact size/
depth boundaries with no partial acceptance. Read-only preview must work without
adapter/SecretStore/external Provider capability. Test second-family shared
dispatch, codec node distinction, exact numbers and whole-value redaction.

No production material resolver, formatter invocation/delivery endpoint, reveal,
automatic rollout, list/delete/history cleanup, YAML/Liquid/parameter execution,
multi-document transaction or unrelated CLI redesign. Preserve current full-suite,
restore/fault/race/Native AOT gates and #140 safe measurement artifacts. ADR-033
allows explicit current-alpha reset requirements when needed, never silent reset.
