# Existing Resource import: generic authority foundation

This is the generic foundation of the accepted bounded [issue #267](https://github.com/hamstix/haby/issues/267)
capability under [ADR-062](../adr/0062-existing-resource-import-and-attachment.md).
[PR #280](https://github.com/hamstix/haby/pull/280) delivers the shared intent,
capability and persistence boundaries. Subsequent merged slices deliver
[PostgreSQL pair verification, protected credentials and the authenticated operator RPC/CLI path](267-postgresql-import.md).
Issue #267 was accepted on 2026-11-23; Reference and Provider connection status
remain separate work, and P3 is not complete.

## Exact intent and capabilities

`IDurableDeploymentPlanner.CreateImportChangeSetAsync` accepts an immutable
`ResourceImportIntent` for one exact Environment/Application/ManifestRevision.
The complete manifest Resource set must be present; partial import and mixed
import/create are refused with `ImportNotSupported`. The initial precondition is
ExpectedAbsence. The reusable manifest does not acquire import-specific fields.

Each request carries an adapter-owned, versioned, non-secret payload. Adapters must
validate a closed typed schema, external identities and supported declared state;
secret material must never enter this payload. The intent codec is source generated,
rejects unknown/duplicate fields and versions, and limits the encoded document to
1 MiB. Individual inputs are bounded, with at most 256 exact Resource requests.

`DurableActionAdmissionKey` includes `ResourceAcquisitionMode`. Provision and
ImportUseAndRetain registrations are distinct for the exact ResourceType and Provider
kind/configuration/implementation. A missing import registration returns typed
`ImportNotSupported`; a Provision registration cannot supply fallback support.
Import operations require actual dispatched attempts and an exact registered external
output contract. Generated-credential operations cannot supply import evidence.

Plan freezes intent, acquisition mode, Provider revision and adapter input in the
operation graph and fingerprint. Check and Commit revalidate the separate ChangeSet
intent. Reload reconstructs the same frozen inputs. Output publication, checkpoint
completion and later continuity verify persisted acquisition authority against the
original source generation.

## Provenance and authority

The shared management association stores immutable `ResourceOrigin` and
`ResourceLifecycleAuthority`, with a paired database constraint. Initial Commit records
Imported/UseAndRetain for imports and Provisioned/Provisioned for provisioning. The
association alone does not confirm a usable Resource: successful real import attempts,
validated ordinary outputs and ResourcesReady evidence are required.

UseAndRetain never grants ownership, permission-repair, rotation or deletion authority.
Updates require unchanged Resource type, Provider alias, deletion policy and properties,
the same exact Provider/naming inputs, and explicit policies from the original
acquisition-mode registration. Reuse and retention preserve the direct original source;
target-generation Provision capabilities do not authorize imported Resource reuse.
Unsupported mutations refuse before acceptance. Journal reads and output/checkpoint
publication also reject missing or inconsistent persisted authority after restart.

## Catalog transition

This slice uses one current alpha model under [ADR-033](../adr/0033-development-compatibility-and-catalog-resets.md):
operation graph version 6, durable input version 7, configuration planning snapshot
version 4, and version 6 plan fingerprints. Frozen renderer pins preserve and validate
the exact producer acquisition mode.
Catalogs with earlier saved Plans or generations require an explicitly initialized
fresh catalog. Startup preflight and the migration itself refuse that history before
schema changes. Empty supported catalogs can apply the additive migration. No authority
is backfilled, existing catalogs are never reset automatically, and external Resources
are never changed or removed by this transition. Reinitializing a catalog does not
remove external objects or establish import authority for them.

## Validation scope

`ResourceImportIntentTests` cover the closed codec, document bounds, exact type/mode
refusal, complete Resource coverage, frozen intent/mode tampering and update-baseline
refusal. `ResourceImportMigrationTests` check model/snapshot consistency and the
migration's pre-mutation refusal. `ResourceImportAuthorityTests` use a provider-neutral
fake adapter with a real isolated PostgreSQL catalog, separate arrange/act/assert
contexts, Plan/Check/Commit/Execute, service restart, confirmed outputs, immutable
provenance, unchanged reuse, mutation refusal and lost-authority publication refusal.

These catalog tests do not prove real external import verification, credential
attachment, cross-system atomicity, PostgreSQL import, or operational readiness.
Those require the PostgreSQL slice's separate evidence and exact-revision qualification.
