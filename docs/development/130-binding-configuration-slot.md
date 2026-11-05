# Binding configuration slots and current-alpha catalog reset

Issue #130 implements the current Application `haby.dev/v1alpha1` Binding
`configurationSlot` under ADR-033/038. The schema identity and API version stay
the same. No alpha2 or legacy adapter is published.

The optional ordinary string follows ManifestLocalId rules. Absence is preserved
as null internally, with no inferred slot. Import validates lexical shape and
Component-local uniqueness without consulting Environment Profiles, rules,
SecretStore, renderers or outputs. Direct projections remain independent and
share effective collision checks with Profile projections. External target
syntax remains accepted; emitted executable requests still require local targets.

The immutable normalized model and internal declaration snapshot format are both
**version 2**. Every stored Binding requires `configurationSlot`, including an
explicit JSON null for absence. Omitted members, invalid strings/types and
inconsistent or unsupported versions fail safely. Slot add/change/removal changes
normalized equality and same-ApplicationVersion conflict identity; changed paths
identify only the exact member, without values. Map ordering and existing defaults
retain their semantics. All snapshot readers use the exact current codec.
Planning fingerprints still pin immutable revision references and exact operation
graphs; this slice does not resolve Profiles or incorporate configuration layouts
into provisioning Plans.

`ProfileComponentInput` now receives the normalized Component, decisions and
optional static contributions. Its Bindings are the sole source of requested
slots, including after database reload. Expansion rechecks duplicate slots and
retains direct projection collisions, whole-Profile selection and consumer options.
It emits unresolved requests only; slot membership grants no access or privileges.

## Explicit reset requirement

Catalogs containing superseded normalized model/snapshot version 1 revisions
require an explicitly initialized fresh catalog before reuse with this build.
There is no old-format reader, backfill, compatibility migration or automatic
catalog reset. Existing migration history is not proof that stored snapshots are
readable. Supported import/query/planning paths refuse unsupported snapshots;
they never silently drop a slot or replace an accepted revision.

Do not point reset commands at a persistent developer catalog. The operator must
choose and initialize a fresh catalog explicitly and decide how retained external
resources are handled. Resetting Haby does not delete or roll back external
databases, roles, credentials or other effects. This implementation performs no
reset. Current-build restart, immutable retries, history, backup/restore and
execution uncertainty guarantees remain required.

## Evidence and boundaries

Focused tests cover schema/parser diagnostics, immutable mapping/equality/hash,
slot add/change/remove conflicts, strict snapshot presence and versions, restored
duplicate-slot refusal and direct-plus-Profile projection collisions. Real disposable
PostgreSQL tests import, dispose composition, reload with independent contexts,
compare the full declaration and expand saved api/worker slots with independent
consumer options. An unslotted Binding creates no inferred request. The same
suite checks current-build dump/restore and safe unsupported snapshot refusal.

Profiles remain in-process. Persisted Profiles/assignments, administration,
planning resolution, actual renderers/formatters, configuration materialization,
confidential delivery, new CLI/API operations and generation updates are deferred.
