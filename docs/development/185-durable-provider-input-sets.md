# Durable declaration-keyed Provider inputs

Issue #185 implements C1 of the P3 durable-input sequence. It changes durable
representation and validation. The executable Resource pilot still requires one
alias and one exact Provider context, and retains its existing PostgreSQL shape
restrictions. Workload admission, Placement capture, stage checkpoints, empty
Resource-stage completion and Kubernetes effects remain subsequent work.

## Authority and identity

`DurablePlanningInputs.Providers` is the single authoritative immutable
`ExactProviderResolutionSet`. Every declared Resource has a `ProviderInput.Resource`
key; every Workload has a `ProviderInput.Workload(Component, Id)` key. Identical
local IDs in different Components, and Resource/Workload IDs, remain distinct.
Neither order nor a first alias supplies identity.

Each entry retains its Environment, alias provenance, exact ProviderInstance,
configuration revision and ImplementationId, plus typed versioned non-secret
adapter inputs. The accepted pure-set rules require complete declaration coverage,
unique keys, matching Environment and aliases, and one exact instance/configuration/
implementation per alias. Several declarations may retain that resolution under
their own keys. Omission, surplus, duplication and conflicts refuse.

Resource entries carry `haby.resource-naming/v1`: a closed `snapshot` string
containing the existing generated-metadata ResourceNamingSnapshot, including its
version, policy reference/revision, selection token and portable policy. This
preserves typed integer representation independently of the ordinary configuration
tree's exact-number JSON profile. Common code interprets the existing portable
naming contract; it does not interpret PostgreSQL or Kubernetes fields. Resolved
credentials never enter the set. Opaque adapter trees reject confidential/deferred
values rather than serializing secret material.

The set owns an immutable dictionary and immutable value trees. Its generated JSON
document sorts Resource keys before Workload keys, then Component/local ID using
ordinal comparison. Adapter objects use canonical ordinal member ordering.
Enumeration and object-member order do not change the encoding or fingerprint.
Every entry and its exact adapter contract/value participates in the fingerprint.
Encoding and decoding enforce the same existing 4 MiB UTF-8 document budget.
Zero entries are representable only when the declaration has no Provider inputs;
that fact grants no executable admission or completion authority.

## Durable consumers

| Consumer | Current authority |
| --- | --- |
| Plan, Get, Check and replan | Full set bound to the frozen declaration and configuration snapshot |
| Commit | Locks relevant aliases in stable order; checks every frozen resolution and naming selection before acceptance |
| Operation graph | Exact Resource coverage, declared Resource contract and dispatch references matching the keyed entry |
| Journal and restart | Checked saved generation set; the node's Resource ID selects its sole dispatch target |
| Generated credentials and external outputs | Saved keyed owner/context; no live alias resolution |
| Reuse, retention and reintroduction | Direct original generation's checked set, original evidence, credentials and physical names |
| Configuration materialization | Checked complete generation inputs and their fingerprint, preserving the frozen configuration layout |
| Existing confidential delivery | Original immutable artifact/grant authority; no mutable Provider re-resolution |
| Operator/CLI descriptions | Repeated keyed `provider_inputs`, with exact non-secret adapter contract/JSON; no singleton projection |

`RequireUniformResourceProvider` is an explicit current-pilot capability check: it
requires nonempty Resource-only entries and exactly one equal context across all
entries. It cannot pick an entry from a mixed set. PostgreSQL pair planning retains
this requirement; each emitted operation still selects its Resource entry directly.
Independent input-set storage tests are not an executable multi-Provider capability.
`IDurableActionAdmissionHandler.Validate` now takes the explicit Resource local ID;
registrations must select that entry rather than infer ownership from a Resource
value or alias. `ExactAdapterInput` uses the dedicated `AdapterInputContractId`.
These are deliberate current-alpha plugin-contract changes. Operator clients must
regenerate the replaced descriptive fields; CLI command arguments are unchanged.

The narrow test-only storage seam inserts a multi-entry Plan through the actual EF
mapping and reads it through the production planner after restart. It creates no
generation, journal work, fake Kubernetes Provider or external effect. Production
Plan/Check/Commit refusal and zero desired-work assertions remain separate oracles.

## Persisted-format and catalog boundary

Durable Plan and generation snapshot format is **4**; the exact-set document
format is **1**, and Plan fingerprints use **`v4:`**. The operation-graph document
remains format 2. The normalized manifest uses model/format 3, the configuration
planning snapshot uses format 2, and the portable Resource naming snapshot uses
version 1. Those existing formats retain their own versions. This is one current representation, without an alpha reader,
singleton fallback or automatic backfill.

`PersistExactProviderInputSets` removes the durable singleton Provider columns and
global naming column, replacing them with one `provider_inputs_json` document on
each Plan/generation and an explicit generation snapshot version. The singleton
configuration FKs are removed. Exact references are checked before acceptance and
dispatch; existing unconditional Provider-instance/revision immutability triggers
continue to prohibit their update/deletion. Plan/generation immutability, journal
terminal guards, audit and the remaining generic catalog FKs are preserved.
There are no Provider-specific tables, columns or workflow SQL.

Startup and the migration itself refuse older catalogs containing any durable
Plan or generation before changing this representation. The migration locks both
durable tables before checking, so a concurrent old-format writer cannot slip
between the history check and schema changes. Empty durable history may
cross the boundary. Current-format reads, Check, new Commit acceptance, operation dispatch and
configuration materialization reject incompatible versions, corrupt sets and
fingerprint/graph mismatches. No reset is performed automatically.

Under ADR-033, an incompatible catalog needs an explicitly initialized fresh
catalog. Keep any needed backup/history separately. External databases and roles
survive a catalog reset: a fresh catalog neither owns them nor authorizes their
recreation or adoption. Current-build restart/retry, confirmed-effect evidence and
original-resource continuity remain supported within the current format.

## Validation

Focused tests cover zero/one/multiple and mixed key namespaces, detached snapshots,
independent exact identities, alias conflicts, canonical ordering, naming/version
changes, missing/surplus/duplicate/cross-Environment entries, corrupt persisted
inputs and graph ownership. Real PostgreSQL tests use separate arrange/act/assert
DbContexts. Existing production lifecycle tests retain original OID, sentinel,
credential, original-generation evidence and no-replay assertions.

Run the complete applicable gates in the [local validation protocol](local-validation.md).
Per-SHA results and failures belong in task evidence outside tracked source, not
in this implementation document. Independent review and exact-SHA architecture
acceptance follow the draft PR; this document does not grant merge approval.
