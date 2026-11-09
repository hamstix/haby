# Configuration-only generations without an Application release

Issue #166 implements ADR-045's explicit same-ManifestRevision configuration path.
Start with a fully applied managed Application and authenticated operator context.
Keep the same immutable ApplicationVersion/ManifestRevision, Resource declarations,
Components and Bindings. Use the supported static JSON Profile layout, exact
Profile revisions and registered renderer options. This is not a parameters API.

## Change logging through explicit acceptance

1. Import a new ConfigurationProfile revision with the new static logging content
   and/or permitted consumer defaults. Retain the document/slot relationships.
2. Apply its exact reference at an existing Environment, Application or Component
   scope, using assignment CAS. Component apply requires the explicit existing
   ApplicationVersion. Empty local decisions restore inheritance; Disable suppresses
   an inherited Profile. Selection replaces the whole Profile, without JSON merging.
3. Create a ChangeSet selecting the **same Application record and revision number**
   as the current accepted generation, with its exact ExpectedGeneration. Calculate,
   review and Check a new Plan before explicitly committing it.

```text
habyctl profile import logging-v2.json
habyctl profile assignment apply assignment-v2.json --environment pilot
habyctl changeset create <application-record-id> <same-revision-number> --expected-generation 1
habyctl plan create <changeset-id>
habyctl plan get <plan-id>
habyctl plan check <plan-id>
habyctl changeset commit <changeset-id> --plan <plan-id>
habyctl deployment generation get <deployment-id> 2
habyctl deployment generation execute <deployment-id> 2
habyctl configuration materialize <application-id> --environment pilot --generation 2 --all
habyctl configuration access create <application-id> --environment pilot --generation 2 --component api --document settings --token-output-file <new-private-token-file>
```

Use the [access runbook](148-authorized-configuration-delivery.md) for exact-grant
issuance, protected transfer and receiving-client setup. Issue separate exact grants
for other documents/recipients. A single-document materialization remains available.
No command prints resolved application configuration through the operator surface.

Assignment alone creates no Plan, generation, document, grant or external effect.
Commit accepts DesiredGeneration=2 while AppliedGeneration stays 1. Explicit Execute
checks active and retained reuse evidence and advances AppliedGeneration to 2 with
zero new Resource operations/attempts/outputs, credential versions, SQL grants or ACL
changes. Materialization and delivery-grant issuance are separate explicit actions.
Receipt of new JSON is not evidence that an existing process hot reloaded it.
Workload restart/rollout and explicit retirement/cleanup remain P3 work.

## Exact inputs, no-op and defaults

The new Plan recalculates the complete target layout from current relevant scope
inputs (including absence), exact Profile declarations/revisions, selected options,
renderer/formatter identities, resolved defaults and provenance. It does not copy
baseline configuration because the ManifestRevision is equal. A different exact
Profile revision or relevant assignment token can create a generation even when
rendered values match. An absent scope becoming an explicitly empty assignment is
also a different accepted input. Plan/ChangeSet IDs, actor and calculation time
alone do not change desired state: exact unchanged accepted inputs commit as a
successful no-op at the existing generation, with their own immutable receipt.
Replaying an already committed ChangeSet returns its original receipt even after
later updates; it never creates another generation or rewrites acceptance audit.

A newly calculated Plan resolves currently registered supported renderer defaults.
A saved Plan, generation or document keeps its captured defaults when a compatible
implementation with the same identity changes defaults. Check/Commit validate
compatibility and the saved defaults, without replacing them with current defaults.
Missing/incompatible support refuses. There is no defaults mutation endpoint.

Relevant Environment/Application/target Component assignment changes stale Check
and Commit, including first insertion into an absent scope. Other Applications,
Environments and absent Component scopes do not participate. Existing scope/alias
locks and deployment serialization protect atomic acceptance; competing commits
have one winner. Replan explicitly when intent is still current, or create a new
ChangeSet when ExpectedGeneration no longer matches. No automatic replan/retry.
An incomplete or NeedsAttention baseline cannot be superseded. Provider revision
rebinding, Resource mutation and unavailable original evidence still refuse.

## Immutable artifacts and Resource continuity

Profiles can add/remove documents and contributions under the existing strict
collision/slot/option rules. `--all` uses the target generation's frozen inventory.
Old exact documents/grants keep their old Profile content, defaults and secrets
through subsequent updates, restart and current-build catalog backup/restore.
Latest selection never backfills or overwrites old artifacts. Removing a document
does not revoke its old grant or stop a running recipient. Explicit expiry/revoke
and future P3 retirement remain separate.

Configuration-only acceptance also works after mixed creation and retained removal.
Stable management, name ownership, original successful attempts/outputs and direct
sources remain unchanged (including standalone source generation 2). Retained
membership survives the configuration update; later compatible same-key
reintroduction still uses the original source, with no namespace release/adoption.
The #160 and #162 addition/Component/Binding paths keep their previous boundaries.

## Storage and bounded evidence

No migration, schema/model/codec version change, reset, backfill or history rewrite
is introduced. Existing typed configuration snapshots already encode the exact
significant inputs and defaults; Commit compares their canonical complete encoding.
The current graph already represents zero-operation active/retained reuse. Catalogs
without #158 management records remain unsupported for updates. Earlier format
admission rules remain unchanged; startup never resets persistent data.

Tests extend the actual authenticated generated-client/production-Host path with
same-revision assignment-only observations, received Logging and typed connection
settings, identifiable DDL/DML, exact equal-output Profile identity, stable-input
no-op/replay, zero operations/history changes, restart before Execute and real
pg_dump/pg_restore. Dedicated tests cover relevant scope insertion/change,
lock-observed competing commits, cancellation around acceptance, invalid target
inputs, mixed/retained continuity and compatible reintroduction. An explicit typed
synthetic queue registration proves new default capture and old Plan/generation/
document freezing through shared planning, composition and storage.

Required published Windows/Linux Native AOT, full solution/PostgreSQL, strict
codec/budget, authorization, dependency and #140 timing/artifact gates remain
unchanged. Exact executed counts/HEAD/CI and unperformed checks belong in the PR;
these descriptions are not a manual production-readiness claim. Secret projections,
Liquid/parameter/YAML/text execution, Provider rebinding/rotation, automatic apply,
materialization/grants and Workloads remain outside this slice.
