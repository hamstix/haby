# Internal UPDATE readiness, publication retirement and Applied completion

Issue [#259](https://github.com/hamstix/haby/issues/259) extends the internal
[#252](252-deployment-update-planning.md)/[#257](257-durable-deployment-update-submission.md)
ADR-058 path. Internal `WaitUpdateAsync` observes an already submitted exact
successor, checks all previous recipients, retires only its frozen obsolete runtime
publication permissions, then advances AppliedGeneration. Public UPDATE admission,
submission and wait remain refused. Corrective generations and the integrated
update/correction rehearsal remain separate; P3 is incomplete.

## Authority and readiness

The internal completion port uses accepted read authority separately from effect
submission authority. It validates the original Applied baseline, frozen target
inventory, exact conditional submission receipts and current object transitions.
Every target Workload must participate in a complete fresh readiness round under
its current observation attempt. Another generation's Ready or an older saved
checkpoint cannot authorize completion. Existing initial completion and the
Resource-only path keep their separate authority.

Original rollout deadlines remain anchored to submission. Restart and new requests
do not extend them. The existing explicit internal observation resume appends a
new attributable attempt only for the expected expired attempt. It performs no
provisioning, configuration preparation, identity creation, publication or PATCH.

Current target publication rights and continuing bindings are checked before fresh
reads and again under the aggregate lock before retirement and Applied. A historical
completion proof is distinct from current delivery authorization. Revoked rights
are never recreated by this port. The ordinary fully-applied configuration selector
keeps its existing InternalEffects exclusion.

## Previous recipients and bounds

The adapter reads the exact saved Deployment UID and target managed projection,
checks the continuing runtime ServiceAccount UID when configured,
then reads complete namespaced ReplicaSet and Pod lists, and finally rechecks the target.
Controller references establish Deployment UID -> ReplicaSet UID -> Pod UID chains.
Target classification uses the owned Pod projection, including the exact bootstrap.
A previous Pod still counts when it has a deletion timestamp. Labels, rollout
counts and availableReplicas alone cannot authorize permission retirement.

Missing owners, unresolved Pod parents, duplicate identities, replacement controller
identity, API errors, cancellation, absent list versions, pagination or remaining
items refuse absence. Namespace snapshots are deliberately conservative: an orphan
or unrelated unresolved Pod can prevent retirement rather than disappear from the
proof. No whole-cluster or unbounded scan is performed.

Internal callers supply `RecipientObservationOptions`: MaximumObjects defaults to
2048 per list (range 1..16384), MaximumResponseBytes defaults to 4194304 per response
(range 1024..16777216). Each list is one bounded request; continuation is refused.
MaximumCalls defaults to 4096 (range 1..8192) for recipient API work; the current
adapter reserves up to five calls per target before beginning its snapshot. Production
HTTP response bodies are bounded before SDK deserialization. Existing
request/read deadlines and call/round budgets remain effective. Each recipient snapshot also uses the original read-timeout cap. Recipient work uses
the original request cancellation/deadline; it cannot create a new wait budget.
The existing 2 MiB checkpoint and complete-set limits still refuse oversized proof
sets rather than omit members. Typed evidence retains bounded UID chains, target/
previous classification, terminating flags and list versions without raw objects,
bootstrap values or credentials.

These are sampled observations across separate systems. They do not establish
perpetual absence or an atomic transaction spanning Kubernetes and the catalog.
An administrator can change the cluster after an observation. Resume requires new
target readiness and recipient observations before further retirement work.

## Durable retirement and completion

For nonempty obsolete publication inventory, a generic `RetirementStarted`
checkpoint commits before the first revoke. It binds the frozen inventory, complete
fresh readiness and typed recipient evidence. Future ADR-052 corrective admission
must fence on this marker even if a response was lost. Read-only observation and
an explicitly empty publication obligation do not install a destructive-start marker.

Every permission revoke is a catalog transaction under the existing deployment
aggregate lock. It rechecks current generation/expected Applied, target rights,
complete preparation, exact old permission/binding tokens and the original
publication receipt. Atomic live-reference checks protect target and other active
accepted preparations, including continuing current references elsewhere. A stable
per-permission request and immutable runtime publication receipt record individual
progress in the same transaction as revocation. Unknown, externally changed or
missing progress refuses; a scalar Applied value or absent permission is not proof.

Restart or response loss resumes validated incomplete work. It neither repeats a
confirmed PATCH nor restores retired rights. Before each resumed changing stage,
the caller freshly observes readiness and recipients; catalog prerequisites are
revalidated under the lock. No external observation or SecretStore call runs in a
catalog transaction. The final transaction saves RetirementCompleted, performs the
expected-prior-Applied/concurrency-token update, and closes the request atomically.

Ready with previous recipients or incomplete cleanup retains the previous Applied
value. Same-request replay is immutable historical evidence and performs no fresh
observation. A new request may freshly confirm an already completed generation
without another Applied transition. Cancellation before a commit prevents its
admission; lost responses after commit never imply rollback.

Only exact obsolete **runtime publication permissions** are revoked. Deployment,
ServiceAccount, runtime binding, documents, history, Resources and credentials stay
intact. ReplicaSet history remains controller-managed. Static P2 grants retain their
independent lifecycle. Authentication remains service-account-bound under ADR-050;
recipient observation adds lifecycle evidence, not Pod-generation authentication.
Document-free successors retain an explicit empty publication obligation and need
no synthetic identity or documents.

## Schema and evidence

The additive migration only admits the generic RetirementStarted checkpoint stage.
No Provider-specific table or column, accepted-input rewrite, proof backfill or
automatic reset is introduced. Existing supported current input formats remain.
Unsupported older alpha formats retain their explicit ADR-033 reset boundary.

Fast tests cover UID-chain classification, terminating recipients, pagination,
ambiguous ownership, snapshot bounds and receipt isolation. PostgreSQL cases use
separate arrange/act/assert contexts for readiness, restart/lost-response boundaries,
partial progress, cancellation, current revocation, complete target coverage and
changed active references. Existing initial completion and UPDATE submission cases
remain regression gates.

The existing configured Kubernetes contour reuses the pinned consumer graph and
image. A fixture-owned finalizer holds one verified old Pod during target readiness;
its UID chain and terminating state must block retirement. The fixture releases
only its own marker under UID/resourceVersion checks with bounded cleanup. Both
normal waits and the intervening production recipient observations share one
absolute 60-second fixture budget. Releasing the held Pod is insufficient: every
previous UID-chain recipient, including terminating Pods, must be absent before
one normal final wait. Observation polling cannot submit lifecycle actions, renew
attempts or extend the original rollout deadline. Expiry reports only the last
bounded recipient counts/categories and a fixed expiry marker. The fixture then
requires exact old-right denial, retained target delivery and Applied completion.
After all U01-U12 assertions, separately labeled fixture teardown quiesces only
catalog-receipted Deployments with fresh namespace/UID/ownership checks and
UID/resourceVersion-guarded scale-to-zero. Complete namespace Deployment,
ReplicaSet and Pod lists refuse unknown identities and include terminating,
pre-restart and replacement Pods. Delivery and drain share the same existing
absolute 35-second restart-delivery token, followed by the unchanged 20-second
namespace cleanup. No grace override, command/image change or new deadline is
introduced. Exhaustion fails explicitly; an ownership conflict also refuses the
namespace-delete fallback. Other failures retain bounded owned cleanup and
separate functional/drain/cleanup attribution. This scale is qualification cleanup,
not product UPDATE evidence.
The existing Recreate/RollingUpdate venues remain; no new image, cluster mode,
PostgreSQL matrix or load campaign is added. Test inventories are obligations,
not passing evidence. Executed results, unperformed checks, current full SHA and
mandatory CI links belong in the author report and draft PR.

## Qualification boundary and failure diagnostics

This slice qualifies **first UPDATE completion only**. Successive UPDATE remains
unqualified: initial -> update1 Applied -> update2 Applied must prove retained
origin receipts, retired permission rows and current-object state before public
operational UPDATE or an ADR-058 operational declaration. That follow-up is not
implemented in parallel here. Public UPDATE remains closed.

The existing contour records a fixed returned/threw stage for the entire second
wait, Ready/Applied/expected-generation booleans, closed reason/stop values,
recipient counts and the first fixed projection failure category. Catalog closure
records the actual called stage and a closed classification of existing static
refusals. No duplicate acceptance validator or fallback acceptance is introduced.
U11 checks Ready, Applied and expected generation separately, retaining all three
requirements. Primary and cleanup failures are distinct; existing reads report
only whether the fixture-owned finalizer is present. No budget, deletion scope,
normalization predicate or runtime/public contract changes follow from diagnostics.
