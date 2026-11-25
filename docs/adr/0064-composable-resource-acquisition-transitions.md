# ADR-064: Composable Resource acquisition across Application revisions

- Status: Accepted
- Date: 2026-11-25
- Amends: [ADR-062](0062-existing-resource-import-and-attachment.md) and [ADR-063](0063-resource-capabilities-rendering-and-provider-status.md)
- Implementation: Three sequential slices: [#303](https://github.com/hamstix/haby/issues/303) -> [#304](https://github.com/hamstix/haby/issues/304) -> [#305](https://github.com/hamstix/haby/issues/305), after #292; acceptance is not runtime support.

## Context

Adopting an existing application requires successive dependencies to be attached
without resetting its catalog or repeating accepted imports. Initial Import and
Reference support alone does not provide this transition. At main 0b77e82,
public acquisition intake and planning reject additive Reference updates outside
the existing Workload preparation shape, while reuse assumes a uniform Resource
Provider. Existing continuity, addition policies and durable completion are the
foundation to extend, not replace with a parallel planner.

## One transition calculation

The normal path is immutable Application revision -> Plan from accepted current
state -> Commit -> Execute. The common layer matches stable resourceRef identities
within Environment/Application and classifies unchanged, added, changed and removed
Resources. Renaming is removal plus addition, never automatic identity transfer.
Components, Bindings and frozen Profile/configuration dependencies belong to the
same target state; a consumer configuration change is not itself an external
Resource mutation.

Resolve exact Provider instance, configuration revision, implementation and typed
capability per Resource. There is no uniform-Provider requirement or allowlist of
Provider combinations. Adapters interpret typed properties and validate coupled
Resources; the common layer must not understand database, role or Redis semantics.
Use existing planning, durable graphs, persistence and adapter contracts. Do not
introduce a general plugin framework, arbitrary metadata bags or Provider-specific
catalog tables/columns.

## Accepted functional boundary

One ChangeSet supports reuse of unchanged Resources, new Reference Resources and
new ImportUseAndRetain Resources. A new Application supports an initial mixture
of ImportUseAndRetain and Reference without a preliminary PostgreSQL-only revision.
Updates require a fully Applied baseline and the existing expected-generation CAS.
New Resources may select different Providers. Existing Resources retain exact
Provider inputs, credentials, acquisition mode, provenance and lifecycle authority.
Reuse is a planning decision, not a new acquisition mode or implicit Provision.

Retain existing supported transitions. This extension does not implement general
Resource UPDATE, removal/retirement, replacement of an existing Resource's Provider
or credentials, or Workloads. Classify these changes and return concrete typed
Resource-specific refusals instead of rejecting arbitrary Provider combinations.
Reference retirement remains separately tracked in #301.

## Public intake and protected credentials

Use one public ChangeSet creation path with target ManifestRevision, precondition
and acquisition declarations for NEW Resources only. All Resources are new for an
initial Application. The server derives retained acquisition inputs and exact
credential references from the confirmed baseline, then validates complete target
coverage. Reject missing, extra, duplicate or conflicting declarations. Existing
Resources never require password resubmission and cannot acquire a caller-selected
new origin through this path.

Credential material enters a separate protected intake. ChangeSets carry opaque
intake references, never passwords. Import capabilities declare typed credential
slots, purposes and owner rules. Intake binds credentials to the exact Environment,
Application, Resource and import request. An arbitrary Environment secret cannot
masquerade as an imported application credential. Synthetic adapters use this same
intake and authority path, not internal seeding or test bypasses.

Reference credentials come from exact Provider inputs with explicit application
use authority. System access does not imply application delivery. Preserve current
secret redaction, per-request authorization and source-generated serialization.

## Group confirmation and completion

An adapter may require a coupled set of new Resources to be verified together.
Complete required group checks before publishing its confirmed associations and
outputs. Across Providers there is no distributed transaction: completed groups
remain durable for retry, but the generation and its configuration are not made
available until the entire target state is confirmed. Never delete external
objects to compensate for partial import failure.

Reuse retains original evidence, outputs and acquisition authority and performs
existing current-state/adapter checks; historical Applied alone is insufficient.
Missing capability, drift or revoked authority fails explicitly without CREATE,
external repair or silent reimport. Plan pins baseline, reuse references, modes,
exact Provider inputs, secret references and configuration dependencies. Check and
Commit retain their applicable revalidation and concurrency rules.

Aggregate completion includes reused and newly acquired Resources. Failure leaves
the prior Applied state intact; committed desired state and durable attempts may
remain pending under existing recovery semantics. Retry/restart preserves original
identities and history. Partial new-generation outputs cannot authorize premature
configuration delivery. Materialization and access issuance remain separate
operations under the existing authorization contract.

## Staged correction

Before Commit, a new revision and ChangeSet may explicitly replace a staged request
using the existing correction model. Preserve its history. Transfer permitted
credential references and release superseded request reservations atomically when
the replacement is accepted, using the existing Commit/CAS acceptance boundary;
creating or planning the replacement does not retire the predecessor. Validate
owner, purpose and exact target before any transfer. Concurrent acceptance admits
one winner. After Commit use durable retry/recovery; never silently replace inputs.

## Delivery sequence and acceptance

See [implementation and review tasks](../development/resource-transition-composition.md).
The three slices are required, ordered after #292; A alone does not complete this ADR.

1. A: common delta and per-Resource Provider resolution; Applied imported Resources
   plus new Reference, configuration-only. Real PostgreSQL import followed by a
   synthetic Reference from another Provider must produce authorized combined output.
2. B: add ImportUseAndRetain through real protected typed intake to the Applied mixed
   state from A. Preserve all old Resources/credentials/authority. Design intake for
   both baseline-present and baseline-absent cases; do not build a second import path.
3. C: initial mixed Import plus Reference through the public API, without a PostgreSQL
   staging revision. Complete public admission and integrated partial-failure/restart
   qualification using the same mechanism.

Use PostgreSQL and synthetic typed adapters upstream. Production Identity, Redis,
S3 and product conventions stay downstream. Verify drift, missing capabilities,
protected credential ownership/purpose, exact retry, concurrent baseline acceptance,
restart and no premature delivery. Tests must demonstrate capabilities compose,
not merely that isolated adapters pass. No runtime qualification is claimed here.
