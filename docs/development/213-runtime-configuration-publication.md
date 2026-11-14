# Internal exact runtime configuration publication

Issue #213 adds a friend-only metadata permission boundary after the accepted
[#208 preparation](208-preapplied-workload-configuration.md) and
[#211 stable identity](211-stable-workload-runtime-identity.md). It creates no
externally callable endpoint. TokenReview, delivery clients, projected-token
injection, configured Deployment execution, readiness and Applied remain closed.
Static Profile secret projections remain unsupported under pending #209.

## Exact authority and separate states

`InternalRuntimePublication.PreparationRequestsAsync` derives every recipient from
the actual accepted Commit, complete saved document associations, frozen selection,
ResourcesReady proof, input fingerprint and confirmed stable identity inventory.
It validates all selected subjects before returning any requests. A caller-supplied
artifact UUID, materialization alone or a foreign binding cannot publish a document.
Every new publish revalidates the full recipient and immutable identity proof inside
its acceptance transaction. Current admission still permits only genuine initial
Workload-only and Resource-baseline mixed generations; it does not fabricate a
successful prior Workload rollout to admit another generation.

| Record | Meaning |
| --- | --- |
| Publication request and receipt | Immutable exact intent, original outcome, origin and actor/time audit |
| Runtime configuration permission | Current Published/Revoked state and concurrency token for one binding/revision pair |
| Runtime identity binding | Immutable identity with independent Prepared/Revoked lifecycle, token and revocation audit |

`Prepared` alone grants no access. An original `Published` receipt proves what that
intent did, not current permission. Equivalent replay returns the original receipt
alongside a coherent current binding/permission snapshot. History remains readable
after revocation; replay changes no token, timestamp, audit or permission. Changed
requests under the same key refuse without inspecting a caller-substituted target.

Each binding/revision pair has one stable permission ID. Different Workloads need
different bindings and permissions even when they select one shared artifact.
Multiple exact document revisions remain independently representable. This storage
shape does not enable cross-generation rollout or implicit latest selection.

## Conditional operations and replay

The internal operations are PublishPermission, RevokePermission and RevokeBinding.
Every valid operation has a bounded explicit request key and durable result.
Permission operations require expected absence (null token) or the exact current
permission token; binding revoke requires its exact current binding token.
Preconditions are checked before same-state equality. A stale request records
PreconditionFailed, even if its desired state currently matches. A valid same-state
request records Unchanged and preserves state token and transition audit.
Revoke of an absent permission under expected absence is a durable no-op.

Automatic preparation uses a versioned deterministic key from exact deployment,
generation, Component, Workload, document and revision. Its initial precondition is
always expected absence. Resume never chooses a fresh token or random request to
repair a revoke. Reopening a revoked permission requires a new explicit internal
intent and the current permission token, while the binding is still Prepared.

Binding revocation is terminal in this slice. It disables every permission without
deleting any permission, document, identity, inventory or receipt. No operation can
reactivate the binding, remap its account UID or create a replacement ServiceAccount.
Stable identity replay preserves and validates the complete original CREATE proof;
preparation separately reports revoked runtime authority. Unknown or corrupt
lifecycle state fails closed. P2 static delivery grants and tokens stay independent.

## Transactions and partial completion

Mutation lock order is a transaction advisory lock on the request key, followed by
the existing deployment row lock. All permission and binding mutations share that
deployment serialization. The request lock also covers same-key/different-target
races. Equivalent replay reads current state under the same deployment lock.
New permission transition, immutable receipt and audit commit atomically.
No SecretStore read, rendering or Kubernetes/network call runs inside a catalog
transaction. The original identity's terminal receipt is validated without API I/O;
the existing identity execution path retains its own known-account observation.

Sequential preparation has one transaction per permission. It reports completed
members and the first failed/not-attempted member; it never promises whole-batch
rollback. Cancellation before commit can roll back that member. A committed result
is retained even if its response is lost: inspect/replay the same exact key. A
storage error after commit is not proof that the permission was never published.
EF/provider failures are translated to a safe storage error without driver detail.
For a member whose result is unavailable, `RequiresInspection` is true and `Failed`
identifies that exact unconfirmed request, not a promise of rollback. The result
retains the known committed prefix. Inspect/resume with the same request identity;
never invent a new intent to recover an unknown commit outcome. A failure during
the upfront read-only selection returns no attempted member and an empty prefix.

`AuthorizeMetadataAsync` checks current binding lifecycle and active exact permission
in one RepeatableRead database snapshot. It returns only the exact non-secret
artifact context for a future authenticated caller, with no document body or secret
resolution. It is not authentication and is not registered with Host. There is no
positive permission cache. Authorization before revoke may finish; authorization
starting after revoke commits refuses. A retained earlier context does not authorize
later requests.

## Storage and validation

Generic `runtime_publication_receipts` and `runtime_configuration_permissions`
use exact foreign keys, unique binding/revision pairs, closed states, nonempty UUID
tokens and source-generated version-1 DTOs. Keys are at most 128 UTF-8 bytes;
persisted receipt/origin envelopes are at most 16 KiB each. Actor validation follows
the existing planner boundary. No bearer material, token hash or Provider-specific
catalog column is added.

The additive migration initializes only lifecycle tokens for existing Prepared
bindings, using a temporary PostgreSQL UUID default that is then removed. It does
not synthesize origin/effect receipts, backfill superseded Workload history or reset
the catalog. Existing unsupported old-Workload refusal remains. Current-build
restart and dump/restore preserve receipt IDs, audit and revocation independently
of immutable account identity. An independently active clone remains unsupported
without an explicit future identity/binding transition.

Focused tests cover actual initial/mixed service preparation, shared artifacts,
foreign/missing/corrupt evidence, restart/restore, same-key races, conditional
publish/revoke, binding-wide revoke, fresh metadata authorization and partial
cancellation/response loss. Persisted assertions use independent DbContexts.
The race cases separately cover first publication versus permission revoke and
revoked-permission republish versus permission revoke, with truthful stale/no-op
results. Later-member EF save and commit-acknowledgement faults assert the retained
prefix, exact uncertain target, durable audit and same-intent inspection/resume.
The existing real Kubernetes contour retains D01-D32 and adds D33-D36 for exact
publication from genuinely established accounts and revocation-safe origin replay.
This is metadata/API establishment evidence, not runtime delivery acceptance.

Use CONTRIBUTING solution validation and the existing hosted Docker full PostgreSQL
entrypoint. Both published Native AOT gates and the current API/Deployment/durable
regressions remain mandatory. No new launcher, runtime, local SSH/WSLc prerequisite
or 150-client load gate is introduced. Exact-source results, failed attempts and
cleanup outcomes belong in task/CI evidence; this guide makes no merge-readiness
claim.
