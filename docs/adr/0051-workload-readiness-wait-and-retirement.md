# ADR-051: Workload readiness, bounded waiting and retirement completion

- Status: Accepted
- Date: 2026-11-11
- Scope: Fifth P3 Workload decision packet; design acceptance, not implementation
- Extends: ADR-026/032/033/035/045/046/047/049/050

## Context

Submission, runtime readiness and retirement are different observable outcomes.
An administrator may repair external infrastructure (for example, an existing
imagePullSecret with invalid private-registry credentials) and then explicitly
resume or wait again without changing accepted desired inputs or replaying
confirmed provisioning. Product upgrades need bounded batch waiting as a normal
operator feature, not merely a CI convenience. The CLI remains API-only.

This ADR accepts those policies. It does not implement Workloads or authorize
superseding an incomplete generation by itself. The separate bounded corrective
policy is accepted in [ADR-052](0052-corrective-generations-for-incomplete-workload-rollout.md).

## Decision

### 1. One workflow with distinct checkpoints

```text
ResourcesReady
  -> ConfigurationPrepared
  -> WorkloadsSubmitted
  -> RolloutReady
  -> RetirementCompleted
  -> AppliedGeneration
```

These are checkpoints in one accepted generation, not independent deployments.
RolloutReady means the target Workloads meet their runtime criteria. AppliedGeneration
advances only when every mandatory stage, including planned retirement, completes.
Applications without Workloads retain the established Resource-only path.

### 2. Submit without waiting

One server execution request can perform Resource work, exact preparation and
Kubernetes submission, then return a durable Submitted result without waiting for
readiness. A partial submission returns its confirmed prefix and unfinished work;
it does not mark the generation applied. No implicit background worker is promised.

### 3. Exact server-side wait

Provide a separate operator request/command for an exact deployment and generation.
The server observes readiness and can complete retirement/finalization. The CLI
never contacts Kubernetes, loops over external nodes, implements Kubernetes polling
or replans locally. A CLI timeout/cancellation stops its observation request, not
the accepted desired state or effects already dispatched.

### 4. Readiness is tied to saved external identity

Observe saved UID, ownership and managed-state projection, and ensure the controller
has processed the confirmed target Kubernetes generation. A similarly named object,
foreign UID or unexpected managed-field change cannot satisfy readiness. Provider,
namespace and target inputs stay exact; observation does not follow current aliases.

### 5. Deployment completion, not a single condition

Require the desired number of updated, ready and available replicas, no missing
replicas and completion of replacement of old active replicas. Honor configured
minReadySeconds. Available=True or Progressing=True alone is insufficient; conditions
may describe minimum availability or previous progress rather than the exact rollout.
See [Deployment completion](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/#complete-deployment).
Concrete normalization of absent status counts and supported API versions belongs
to the adapter contract and real-cluster tests, not generic Domain conditions.

### 6. Probes bound the promise

Haby confirms Kubernetes readiness, not product correctness. If probes do not
validate database connectivity or migrations, readiness does not imply those checks
ran. Product smoke tests may follow in CI/operator release workflows. Document this
boundary and the effect of configured probes/minReadySeconds.

### 7. Separate rollout and wait deadlines

Persist the rollout observation budget/deadline; Host restart and automatic retry
must not silently restart it. A caller's wait deadline only limits that request.
A caller timeout is not proof the rollout failed, and a subsequent request observes
the same exact generation. Concrete defaults/ranges belong to the Placement and
executable observation contracts.

Expiry retains the prior failed/expired observation and diagnostic history; it does
not make the desired generation permanently impossible to complete. After an external
repair, an explicit resume can create a new bounded observation attempt with audit
and deadline, without a new desired generation. A repeated wait can confirm an
already recovered ready state. Do not erase the original deadline or turn request/
Host restart into hidden infinite waiting. Changing accepted desired inputs is
separate corrective-generation work, not an observation retry.

### 8. No automatic rollback

Wrong image, failed probes, insufficient resources or exhausted observation budget
produce a clear incomplete outcome. Do not automatically restore the previous image
or infer unchanged external infrastructure from the previous AppliedGeneration.
Kubernetes ProgressDeadlineExceeded also does not itself perform rollback:
[failed Deployment](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/#failed-deployment).

### 9. Resume confirmed work without replay

Explicit wait/resume uses the saved generation and repeats no confirmed Resource
provisioning, credential generation or Kubernetes submission. It does not republish
revoked permissions. External problems such as invalid image-pull credentials may
be repaired by an administrator, followed by resume/repeated wait that observes
recovery and completes the SAME generation. Do not call such an external repair
permission to overwrite an unexpected managed-field edit under ADR-049.

Temporary Kubernetes outages permit bounded observation retries. Uncertain writes
must first follow ADR-049; neither wait nor registry-secret repair authorizes blind
write replay, invented success or relaxation of identity/evidence checks.

### 10. Batch wait supports product updates and CI

Provide one server request over an explicit bounded list of exact deployment/
generation references, with per-member results and overall success only when all
required members complete. This is a normal product-upgrade/operator scenario,
not only a CI feature. A failure must not hide other members' results.

Bound list size, observation concurrency, API request rate, response size and total
request deadline. Multi-Application ChangeSets are not required. Batch waiting
neither commits new generations nor implies atomic rollback of the product.
Define concrete limits, pagination/result handling and wire/CLI syntax before code.

### 11. Terminating recipients require separate evidence

New replicas becoming available does not prove old processes have exited.
Before retiring old runtime access, observe completion of the relevant previous
recipients, including terminating Pods, not only availableReplica counters.
See [terminating Pods](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/#terminating-pods).
Kubernetes Pod/ReplicaSet observation for retirement does not strengthen runtime
request authentication beyond ADR-050's service-account-bound mode.

### 12. Plan the retirement inventory

Freeze explicit obsolete owned objects, obsolete runtime publications, retained
objects/permissions and safe ordering/dependencies in Plan. Never infer deletions
from the whole namespace, label selector or only the most recent API listing.
Keep distinct Component/Workload/object ownership and exact UID checks.

### 13. Retire obsolete publications after safe transition

For a continuing Workload, confirm target rollout and completion of old recipients
before revoking obsolete runtime publications. Preserve a document still required
by target Workload or another active accepted preparation. Shared/current reference
requirements must be checked atomically at permission retirement.

Keep its stable ServiceAccount and binding. Publication is not Pod-generation-bound:
old Pods could read new published revisions under ADR-050. Do not invent stricter
authentication semantics while implementing retirement. Commit or first-ready Pod
alone cannot revoke old access.

### 14. Explicit Workload removal order

After remaining target Workloads are ready:

1. Remove owned routing for the obsolete Workload according to Plan dependencies.
2. Delete its obsolete Deployment with exact UID preconditions.
3. Confirm its Pods have terminated.
4. Revoke its runtime binding.
5. Delete its owned ServiceAccount.

Order concrete Ingress/Service changes by their dependencies; DELETE acceptance is
not observed absence. Foreign objects, referenced volumes/Secrets/ConfigMaps and
retained databases/logins stay untouched. Runtime access persists for still-running
recipients until the agreed retirement boundary. Workload removal does not itself
authorize destructive Resource deprovisioning or complete Application removal.

### 15. ReplicaSet history stays with the Deployment controller

For a continuing Deployment, Kubernetes manages historical ReplicaSets using an
explicit revisionHistoryLimit. Do not add an independent generic Haby collector.
No automatic rollback is promised. A manual historical ReplicaSet rollout can refer
to retired configuration permissions; historical Kubernetes revision availability
does not override Haby authorization or constitute a supported rollback workflow.

### 16. Retirement is mandatory and resumable

If target replicas are ready but obsolete-object deletion or permission revocation
is incomplete, report RolloutReady=true, RetirementCompleted=false and retain the
previous AppliedGeneration. Explain the unfinished item and safe next action.
Resume continues retirement without repeating rollout/provisioning. No silent
success/skip-cleanup switch, foreign-finalizer removal or destructive forced repair.

Revalidate relevant current readiness/identity and safe recipient conditions before
continuing destructive retirement after a pause; a historical readiness checkpoint
alone does not authorize unsafe cleanup if the target has since changed or failed.
This is not a promise of perpetual post-completion reconciliation.

### 17. Static P2 grants have an independent lifecycle

Initial P3 runtime cleanup retires its own publications, bindings and owned
Kubernetes objects. Static delivery grants issued by an operator are not revoked
merely because a Workload updates/disappears. Their explicit grant administration
remains separate. Do not claim old static access has been retired by revoking a
runtime publication for the same document.

### 18. Real lifecycle evidence

Prove install -> submit without wait -> separate wait -> image/config update ->
old/new coexistence -> readiness -> retirement -> Workload removal on a real cluster.
Cover stuck terminating Pods, refused DELETE, response loss, restart, failed probes,
external imagePullSecret repair followed by same-generation completion, bounded
product/CI batch wait, cleanup retry and retained-data preservation. No client-side
Kubernetes access, fake readiness or unperformed cleanup success is acceptable.

[ADR-055](0055-p3-implementation-closure-and-coordination.md) refines these
contracts: wait never starts unsent provisioning; resume can continue authorized
unfinished stages; batch wait is limited initially to 200 unique exact members.
A retirement-start marker is persisted before the first destructive action,
and a different UID after deletion remains a foreign-replacement conflict.

## Follow-up contracts and limits

Executable status/error/timeout/batch contracts, immutable retirement inventory,
recipient observation and atomic publication/reference checks require dedicated
slices. Shared execution/persistence stays Provider-neutral; Kubernetes API types,
status criteria and object dependencies belong to the adapter. Keep source-generated
metadata, C#/EF workflow and existing authentication/PostgreSQL/Native AOT gates.

A bad accepted image/manifest can require different desired inputs rather than
external repair. Ordinary ADR-045 updates still require a fully applied baseline.
[ADR-052](0052-corrective-generations-for-incomplete-workload-rollout.md) accepts
a separate explicit corrective generation after confirmed ResourcesReady, without
uncertain writes and before retirement. Implement that contract deliberately;
wait/resume never changes accepted image/manifest contents.
