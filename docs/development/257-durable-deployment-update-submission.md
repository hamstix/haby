# Internal durable Deployment update submission

Issue #257 extends the internal [#252 planning foundation](252-deployment-update-planning.md)
under [ADR-058](../adr/0058-bounded-deployment-image-updates.md). It prepares and
confirms submission of a bounded successor to a Haby-created, fully Applied
Deployment generation. This is **not operational UPDATE completion**. Public
operator UPDATE Plan, Check, Commit and submission remain closed. P3 is incomplete.

## Authority and preparation

Preparation/submission uses a separate internal stage capability. It verifies the
accepted Plan/commit, current desired generation, unchanged Applied baseline,
frozen complete Workload/Resource inventory, original completion proof and exact
Provider/Placement inputs. Initial CREATE and empty-retirement authority cannot
authorize UPDATE or advance its AppliedGeneration.

The adapter reconstructs the typed image delta from the accepted original inputs,
intent and receipt. The original UID, managed identity, image and all nonimage
settings must agree. Missing or forged proofs refuse before dispatch. Execution
does not resolve current assignments, mutable aliases, latest documents or secrets
again. An unchanged image with a new source revision retains `UnchangedImage`.

Resources use the existing evidence-bearing ResourcesReady checkpoint and exact
original producer proofs. Empty Resource stages remain explicit. No database,
role, generated credential or SecretVersion is provisioned, rotated or relabeled.

Every selected document receives a new exact revision for the successor, even
when rendered bytes are equal. Retries within that generation reuse its stored
preparation and complete recipient inventory. Materialization grants no access.

Configured Workloads reuse their original ManagedWorkload, ServiceAccount UID,
binding, audience and identity creation proof. Identity reuse performs live
read-only observation, never CREATE or identity repair. New publication receipts
record successor use while retaining the original identity operation/generation.
All selected exact new revisions must be published before PATCH. Old permissions
remain published in this slice. Revoked bindings, replaced accounts and loss of
required old or new permissions refuse new effects without resurrection.
Unconfigured Workloads need no runtime identity, publication or artificial
configuration preparation.

## Conditional external boundary

The versioned `haby.kubernetes.deployment-update/v1` action uses the closed v4
Deployment intent/receipt codecs. Its immutable target combines the accepted
delta and verified exact preparation. CREATE registrations reject successor
authority; UPDATE registrations reject initial authority. One target in a
generation has one accepted update action. Same-request retries retain its
original action; a competing request conflicts.

The kernel validates the same UID, owner, selector and full prior owned projection,
including exact bootstrap, token audience/volume/mount security and stable account.
It reads the actual resourceVersion immediately before PATCH and tests UID,
resourceVersion and the named main-container index. Bootstrap updates additionally
test the named environment-variable index and its previous exact value.

Only changed owned leaves, exact new bootstrap/document references and operation
metadata are patched. The API response and subsequent observation must match the
complete target projection. Sidecars, foreign metadata, unrelated fields and API
defaults survive. Exact Recreate/RollingUpdate settings remain frozen. Missing
objects, replacement UIDs and owned drift never trigger recreation, adoption,
force overwrite, broad PodSpec replacement or Secret publication.

The common journal persists its dispatch marker before calling Kubernetes and
holds no database transaction across the request. Cancellation, lost responses
and restart after that marker retain uncertainty. Continuation only observes the
exact same target; it never blindly sends another PATCH. Absence or the previous
projection cannot establish success. Server-confirmed precondition rejection
retains the existing bounded kernel reread/revalidation policy.

Successful receipt, operation confirmation and current-object transition commit
atomically. `managed_object_states` is a Provider-neutral projection with original
and confirming action references and a typed adapter payload. Original
`owned_objects`, receipts, attempts and generation history are unchanged. Historical
initial completion hashes cover their own action generation; successor history
does not invalidate that proof or renew any current permission.

## Validation and catalog boundary

Focused tests cover configured/unconfigured preparation, multiple selected
documents, exact recipient inventory, stable identity, same-UID transition,
immutable history, restart, dispatch-boundary interruptions, cancellation,
observation-only uncertainty, duplicate action refusal and unchanged Applied.
Security tests exercise configured prior UID/owner/image/bootstrap/token-layout
drift before any write. Existing CREATE and public lifecycle suites remain gates.

The existing configured Kubernetes consumer contour also exercises an internal
successor after a genuinely completed initial baseline. It preserves all prior
contour assertions, uses the already-published `inputs-b7cb2fbcd77b062869a9c450003da26f87dfc5477750d79d7ef1a71bacde4b24`
tag with the same pinned digest, and changes the resolved reference without
building another image. It checks the real conditional payload, API response,
same-UID readback, injected sidecar/metadata/default preservation, lost-response
observation, stable-identity startup fetching new exact revisions and retained old
permissions. This test does not claim UPDATE readiness or Applied completion.
Executable assertions are not executed evidence: exact results and current-SHA
mandatory CI belong in the author report and draft pull request.

The schema migration adds only the neutral current-object state table. Graph
format 5 and durable input format 6 are unchanged. No historical receipt backfill
or automatic catalog reset occurs. Older unsupported alpha formats retain the
explicit fresh-catalog requirement from #252/ADR-033; resetting a catalog never
removes external Resources.

## Remaining lifecycle

New readiness, observation of all old recipients including terminating Pods,
obsolete-permission retirement and Applied advancement remain separate ADR-058
stages. ADR-052 corrective generations and the integrated real lifecycle rehearsal
remain required before public operational UPDATE. CLI composition follows that
operational path under ADR-059. Retained old rights are a deliberate incomplete
stage boundary, not final rollout completion.
