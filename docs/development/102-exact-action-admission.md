# Issue 102 implementation design

> Historical delivery record. Issue 104 and ADR-033 replace its development-era
> operation addressing and compatibility requirements. Current behavior is in
> [the graph design](104-durable-operation-graph.md).

Base: origin/main e9c8a504b41b9627446c4aeaaa0e6eb77afc4bfc (merged PR #101).

## Shared admission

Introduce DurableActionAdmission and explicit DurableActionAdmissionKey registrations
(ResourceTypeId, ProviderKindId, ProviderConfigurationContractId and
ProviderImplementationId, converted exactly from the retained ProviderAdapterKey).
Typed DurableActionAdmissionHandler<TInput> and DurableActionHandler<TInput> prepare
adapter-owned input;
common code never receives an object target. Common admission enforces one Resource,
no Components/Workloads/exports/parameters. PostgreSQL owns its resource/name/property
checks and PostgreSqlDatabaseCreateInput preparation. Production registers only it.

Replace CreatePlan, Check's current-input reevaluation and Commit's duplicated guards
with the same admission. A persistence helper checks exact instance/Environment,
sealed revision, registered version-aware codec and payload/SecretRef associations
using the caller's DbContext. Commit calls it inside its existing locked transaction;
no new context, transaction, preflight, secret read or Provider call is introduced.
ExpectedAbsence, generation 1, alias comparison, expiry, closure and staleness ordering
remain in their existing coordination paths. Fingerprint and stored schemas stay v1.

## Historical action and exact dispatch

Introduce a distinct versioned OperationContractId and DurableActionExecutionKey.
Explicit build-time DurableCreateActionMapping entries translate saved Resource
contract plus historical Create to an action contract. The PostgreSQL adapter names
that stable contract postgresql.database.create/v1alpha1. It is not a CLR name or an
inference from configuration. Mapping registrations remain independent from installed
handlers. Tests pin the mapping and verify every exact key dimension.

ExactDurableCreateOperationExecutor reads the saved operation first, then the saved
manifest Resource and pinned Provider envelope metadata, never current aliases.
Terminal states return stored history without handlers/configuration decode/secrets.
Dispatching uses the existing journal recovery transition to NeedsAttention before
selection. NeedsAttention stays uncertain. Pending/Preparing without exact support
returns appended HandlerUnavailable (protobuf status 11, CLI handlerUnavailable,
exit 2), retaining actual snapshot/state/reason/attempts and making no external call.
No request-level absence is persisted as an operation reason. Codec availability is
checked independently for admission and by the adapter when execution needs inputs.

## Serialization and retained behavior

A singleton DurableOperationSerialization owns the per-operation gate for the common
dispatcher and direct PostgreSQL facade. The selected handler calls an internal
already-serialized adapter entry point, avoiding recursive acquisition. All execution
branches (including missing support,
terminal reads and Dispatching recovery) use this gate. Journal-only observation
retains its existing nonblocking, read-only behavior while execution holds the gate;
it performs no transition and never selects a handler. The adapter retains its
attempt/dispatch/cancellation/
failure/SQL ordering. ADR-025 manual recovery remains specialized and retains its
existing durable compare-and-transition/audit guards; it is not registered as an action.

## Evidence

Add a genuinely different test-only queue input/handler, codec and exact mapping
through real durable persistence Plan/Check/Commit and the common dispatcher, with a
counting queue boundary. Test shared invalid-input refusal, exact-key mismatch,
startup ambiguity, missing/restored support, terminal/uncertain history and concurrent
same-key calls. Retain PostgreSQL/Host/generated-client/CLI/AOT regression suites.
No graph, payload codec, OperationId persistence, migration or package is introduced.
