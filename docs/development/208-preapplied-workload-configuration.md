# Pre-Applied Workload configuration preparation

Issue #208 adds a friend-only accepted-generation preparation use case. It has no
Host, RPC, CLI command or public request flag. It implements the first preparation
step of ADR-050 section 10; stable runtime identity, publication and configured
Deployment submission remain later work. Preparation never advances AppliedGeneration.

## Inputs and eligibility

`InternalWorkloadConfiguration.PrepareAsync` accepts the actual internal Commit
receipt. The catalog revalidates its exact generation, Plan, ChangeSet, imported
declaration, full configuration fingerprint and administrative revision pins using
`InternalGenerationAuthority`. Initial Workload-only generations require genuine
ExpectedAbsence, generation 1, no Resource obligations, no baseline and null Applied.
Mixed generations retain the supported unchanged Resource set and original fully
applied evidence. No new Resource provisioning shape is enabled.

Every Workload is checked through its exact adapter before document work begins.
The adapter resolves all frozen Placements and object names together, validates
supported effective settings and derives the explicitly selected Component-local
documents. Ordinary static/Profile documents and existing typed Binding/deferred
ApplicationLogin outputs are supported. Profile-provided document definitions are
selected from the frozen expanded layout. Current assignments, aliases and Placement
selectors are never consulted to resume preparation.

Preparation admission is distinct from executable intent admission. The kernel still
rejects any Workload with configuration selection. Action acceptance/loading validates
that executable guard before an intent, attempt, dispatch marker, secret resolution
or API call. An unconfigured internal CREATE retains its existing behavior. Public
Workload planning, committing and execution remain closed.

## Resource proof and document acceptance

ResourcesReady is mandatory evidence, including for an empty Resource stage. The
existing completion boundary can establish the initial checkpoint from validated
accepted inputs and confirmed original Resource results. It cannot reconstruct a
missing checkpoint after documents or recipient associations exist. Corrupt or
foreign proof refuses; a boolean, caller assertion or artifact ID grants no authority.
Existing proof and owner/purpose/contract metadata are checked again on replay and
inside each document acceptance transaction.

The implementation reuses `PostgreSqlConfigurationDocumentStore`, its composer,
renderers, codec, fingerprints, output association checks and unique artifact key:
deployment + generation + Component + document. It creates no second document store.
The new generic `workload_configuration_recipients` table records the exact artifact,
ResourcesReady checkpoint, accepted fingerprint and original audit under a stable
key of deployment + generation + Component + Workload + document. Foreign keys and
immutable application writes retain those associations through restart and restore.

One document and all its selected recipients commit atomically. Two Workloads in one
Component selecting the same document share its single exact artifact. A saved
artifact can acquire a missing valid recipient association without rerendering or
rewriting the artifact audit. Equivalent retries return original IDs and audit;
conflicting source/artifact/association metadata refuses.

Documents commit separately in ordinal Component/document order, with at most 512
selected recipient/document pairs and the existing per-document byte/depth budgets.
A later composition failure or cancellation leaves earlier accepted documents intact.
The result reports the confirmed prefix and the failed/not-attempted exact target;
retry derives the same selection and resumes the remainder. A storage error or lost
response does not prove rollback: repeat the exact accepted receipt to inspect and
converge. There is no batch transaction, automatic effect replay or permission repair.

## Public and secret boundaries

Ordinary operator Source/selection, get/preview, application inventory and materialize
refuse internal pre-Applied generations even after trusted preparation saved an
artifact. This includes existing-result fast replay and concurrent acceptance. The
ordinary fully-applied and exact historical P2 behavior remains separate. Existing
P2 grants and delivery are unchanged; materialization creates no grant/publication.

The architect's 2026-11-14 amendment keeps static Profile `secretProjections`
unsupported. Effective selection returns `profile.secret-projections.unsupported`
before generation/document/recipient acceptance. Parsing, assignment and reading stay
available. No projection is dropped, no administrative secret name is resolved, and
no new secret purpose/catalog is introduced. Contract follow-up #209 owns that work.
Existing typed ApplicationLogin SecretRefs/deferred formatter arguments remain
unresolved under their established exact Resource-owner/purpose/output evidence.
Ordinary previews redact complete confidential nodes. No resolved credentials are
stored in artifacts, associations or ordinary snapshots.

## Catalog and validation

`PrepareWorkloadConfiguration` is additive to the current catalog. It creates the
recipient table and its exact-reference constraints; it performs no backfill, reset,
secret mutation, publication or Applied update. ADR-033 still applies to unsupported
older catalogs. Current-build backup/restore preserves the actual proof, artifacts,
recipient associations and partial preparation state.

The focused persistence cases use real imported/accepted initial and mixed flows
and separate arrange/act/assert contexts. They cover shared-document concurrency,
original audit, authenticated public refusal after artifact creation, restart and
catalog restore, partial cancellation/composition failure, wrong Commit/corrupt
proof, a later invalid subject, frozen Profile selection and unsupported static
secret projections. Mixed coverage consumes the original confirmed ApplicationLogin
output without rerunning producers or regenerating credentials; replay supplies a
failing output reader to prove saved artifacts do not rerender.

Use the solution commands in CONTRIBUTING.md and the existing hosted Docker
[full PostgreSQL entrypoint](193-hosted-postgresql-entrypoint.md). Both published
Native AOT gates and the current Kubernetes API/Deployment/durable-action regressions
remain mandatory. No new runtime, harness, qualification stage or local SSH/WSLc
prerequisite is introduced. Exact-source counts, failures and unperformed gates
belong in task/CI evidence; this guide makes no readiness or merge claim.
