# ADR-066: Workload adoption and explicit Resource management authority

- Status: Accepted
- Date: 2026-11-28
- Extends: [ADR-049](0049-kubernetes-object-ownership-and-uncertain-writes.md), [ADR-050](0050-kubernetes-runtime-identity-and-configuration-publication.md), [ADR-053](0053-forward-correction-without-rollback.md), [ADR-062](0062-existing-resource-import-and-attachment.md) and [ADR-065](0065-explicit-resource-stage-before-workload-advance.md)
- Implementation: separate sequential P3 slices in the [implementation and review cards](../development/workload-adoption.md). Acceptance of this ADR is not runtime support.

## Context and scope

An existing installation must move under Haby management without rebuilding its
databases, credentials or Kubernetes objects. Existing Deployment, Service and
optional individual Ingress coexist with a new ServiceAccount and optional
Headless Service. Resources migrate in the same full Application Plan.

The operator stops the previous workload manager before executing the Workload
stage and explicitly transfers management to Haby. Haby cannot prove that every
external writer is disabled. Permanent shared management is not supported.
Stopping the previous manager is distinct from stopping an existing configuration
source: that source remains available until its consumers have moved.

Shared product Ingress routing several services, arbitrary Kubernetes YAML,
per-object naming overrides, cluster/namespace moves, renaming and replacement by
delete-and-create are excluded. Referenced external ConfigMap, Secret, PVC and
imagePullSecrets do not become owned objects. Unsupported shapes refuse explicitly.

## 1. One installation-neutral declaration and one migration Plan

Keep the full immutable Application revision and version unchanged for installation
binding. Use one ChangeSet, full frozen Plan and deployment generation. Do not make
a hidden Resource-only manifest or fabricate CREATE evidence for adopted objects.

A separate typed installation migration input binds exact Component/Workload
identities to the selected Provider/cluster and namespace and declares adopt/create
actions. UID and observed state are captured by the server, not embedded in the
portable Application manifest. Freeze exact inputs and proof references in Plan;
aliases cannot retarget accepted work.

The expected initial composition is:

| Member | Action |
| --- | --- |
| Existing Resource | Protected Import or explicit supported Reference |
| Existing Deployment | Adopt |
| Existing ordinary Service | Adopt |
| Existing individual Ingress, when selected | Adopt |
| New stable ServiceAccount | Create |
| New Headless Service, when selected | Create |

Actions are explicit. Missing adoption targets and occupied creation names refuse;
neither action silently becomes the other. New object creation uses the existing
typed Kubernetes contract, not a generic object passthrough.

## 2. Resource attachment and management authority

ADR-062 ImportUseAndRetain remains the default import authority. Import itself
only verifies external state, preserves data, identities, password, ownership and
permissions, and publishes confirmed standard outputs. It never repairs external
state to satisfy a policy.

An authenticated operator may explicitly request a separate management-authority
transition in the same migration Plan, without an obligatory second command.
Authority permits only supported changes explicitly admitted by the exact typed
Provider/Resource capability. Unsupported transitions refuse; no generic boolean
may bypass adapter policy or credential-purpose checks. Acquisition mode and
Imported provenance remain original; the authority transition has its own durable
actor, intent and confirmation and never invents CREATE/generated-secret evidence.

The transition grants no deletion authority by default. External Resource deletion
requires a separate explicit permission and operation. Removing an Application
does not implicitly destroy its imported database. Password rotation, ownership or
privilege changes likewise require separate explicit supported operations; accepting
authority neither implements these capabilities nor performs them during import.
Reference does not acquire mutation authority merely by participating in the Plan.

Persist and enforce the resulting authority at planning and consequential execution
boundaries, reuse, restart and deletion/removal paths. Retried transitions converge
on the same result; conflicts do not erase the previous authority. Confirm current
attachment/capability and exact accepted inputs before publishing the transition.
Absence of an explicit transition preserves use-and-retain, including existing
imports. No blanket backfill or implicit authority upgrade is allowed.

## 3. Workload authority, names and identity

Adoption grants normal exclusive Haby management of the accepted Workload through
supported updates and explicit lifecycle operations. This is not permanent
use-and-retain for Workloads, but does not authorize deletion during adoption or
implement missing deletion capabilities. Resource authority remains independent.

Use existing nameOverride or the standard baseName computation:

| Kind | Expected name |
| --- | --- |
| Deployment, ordinary Service, individual Ingress | baseName |
| ServiceAccount | baseName-runtime |
| Headless Service | baseName-headless |

All adopted objects must match these names. Validate kind-specific limits and
collisions without truncation. Do not introduce individual names per object.
Manually configured exceptions outside the declared inventory are neither changed
nor deleted; their presence does not prove successful migration of a required
member. Changing naming identity after adoption remains unsupported renaming.

Save exact cluster binding, namespace, group/kind/name and UID for each object.
Preserve Deployment UID, immutable selector and explicit/default containerName;
Service UID, allocated network identities/ports; and individual Ingress UID and
approved routes, class and TLS references. Names alone confer no authority.
Foreign Haby ownership is a conflict; cross-catalog transfer is not this capability.

## 4. Observed baseline, target and field management

Plan previews adoption/create membership, non-secret exact identity, managed-field
changes, Pod-template rollout changes, strategy and incompatibilities. Do not
persist raw secret-bearing Kubernetes bodies or expose credentials in diffs/audit.
The operator authorizes the verified baseline-to-target transition explicitly.

Full object authority does not mean replacing every field. Retain ADR-049's closed
managed projection, keyed-list handling and preservation of unrelated metadata and
supported injected entries. No arbitrary PodSpec passthrough or silent stripping of
unsupported fields. Unsupported structures with unsafe preservation refuse.

Before each write revalidate UID and meaningful baseline state. Unrelated status or
metadata changes alone need not invalidate the Plan. Use narrowly scoped JSON
Patch with UID and just-read resourceVersion tests; resourceVersion is a write
precondition, not a frozen desired-state value. A significant drift requires a new
Plan, not forced takeover. After adoption, ordinary managed-field conflict rules
apply. Conflicting active controllers are not silently overridden.

## 5. Resource checkpoint and Workload execution ordering

Reuse ADR-065: confirm Resources and exact authority first, record ResourcesReady,
leave Applied unset and wait for separate operator-authorized Workload advance of
the SAME generation. Resource failure does not start Kubernetes mutation.

Before changing the Deployment Pod template, confirm adoption of all selected
existing Workload objects. Then prepare new dependencies and, when selected, exact
configuration publications and runtime access. Partial adoption is durable and
visible; there is no cross-object Kubernetes transaction.

Create the stable ServiceAccount and optional Headless Service without replacing
the existing ordinary Service. Preserve routing to the intended Pods; selector
incompatibility refuses instead of silently redirecting traffic. A Headless Service
may select old Pods before rollout; expose that effect in the Plan.

## 6. Direct and two-stage configuration transition

Support two explicit paths, without assuming that a ServiceAccount changes an
application's configuration client:

1. Direct migration: adopt objects, prepare exact Haby configuration and runtime
   authorization, then update the image and supported startup settings. Configuration
   is available BEFORE new Pods start; readiness completes afterward.
2. Two-stage migration: adopt with the existing image and previous configuration
   source, allowing explicitly planned Pod-template changes such as selecting the
   new ServiceAccount, then use an explicit Application update to move image and
   startup settings to Haby runtime delivery. The old source stays available until
   consumers move. Haby does not administer or automatically migrate that source.

The second path needs an explicit typed configuration-source selection in accepted
Workload inputs. Do not infer mode from missing proof, a failed delivery request or
image name. Legacy-source adoption must not inject Haby configuration bootstrap or
switch the source implicitly. It MAY change the Pod template, including
serviceAccountName, and consequently roll Pods under the selected strategy. Show
these changes in Plan and require target readiness before completion. Creating a
ServiceAccount object alone does not change the Deployment; selecting it in the Pod
template does. Operational metadata alone must not create an unnecessary rollout.
Selecting the ServiceAccount does not itself publish configuration or grant delivery
access. Activate Haby configuration access at the explicit source transition, not
merely because adoption or account selection succeeded.

For Haby delivery retain ADR-050's projected Pod-bound token, exact ServiceAccount
UID/audience/Provider, per-document publication and independent authorization.
Never put operator credentials in Pods. Old runtime publications/recipients follow
normal readiness and retirement rules. Legacy-source completion is not evidence of
Haby delivery; acceptance must also prove the second-stage transition.

Use configured Recreate or RollingUpdate semantics. Changes to serviceAccountName,
image and startup settings can intentionally roll Pods. Operational attempt markers
must not themselves cause rollout. Zero downtime is not guaranteed independently
of strategy, capacity, readiness and application behavior.

## 7. Uncertainty, partial progress and forward correction

Persist exact action intent before dispatch and atomically write operation metadata
with managed changes. Lost response or restart requires observation of UID, owner,
operation marker and exact managed projection before confirmation. New objects use
the existing exact creation-intent evidence. Missing/mismatched evidence remains
uncertain; no blind replay, same-name adoption or automatic recreation.

Resume preserves confirmed adoptions, created dependencies, Resource associations,
credentials and audit. No automatic rollback, deletion of adopted objects or return
to the former manager. Track created dependencies for explicit safe lifecycle work,
not blind failure cleanup. Administrator fixes of operational causes may permit
resume/wait. Changed target declarations require supported forward correction;
this decision does not silently widen image-only corrective execution to arbitrary
migration changes. Unsupported corrections refuse explicitly.

Applied requires the selected target's readiness and existing completion/retirement
obligations. Neither adoption receipts, accepted patches nor ResourcesReady alone
constitute complete Application success.

## 8. Architecture and qualification

Use current planning, exact inputs, durable journal, deployment fencing and shared
Provider-neutral persistence. Kubernetes identities, projections and patch semantics
stay in typed versioned adapter payloads. Resource authority is Provider-neutral;
no PostgreSQL/Kubernetes-specific shared columns or parallel migration engine.

Qualify real public generated APIs against existing PostgreSQL data/credentials and
real Kubernetes Deployment, Service and individual Ingress, with new ServiceAccount
and Headless Service. Prove both configuration paths, same-UID update, stable Service
network identity, Resource preservation, explicit authority, stage pause, delivery,
readiness, drift, foreign ownership, UID replacement, partial progress, response loss
and restart. Reuse existing venues and applicable targeted/smoke/nightly/release
policy; no new load campaign or broad Kubernetes version matrix.

## Consequences and supersession

This explicitly extends ADR-049's initial no-adoption boundary ONLY for approved
typed adoption. Ordinary CREATE still refuses foreign occupied names. It extends
ADR-062's initial use-and-retain boundary ONLY through the explicit capability-
checked authority transition; import remains externally observational. ADR-065's
stage mechanics remain unchanged. All other earlier safety rules remain effective.

Implementation tasks must inventory existing companion support and contract seams,
then deliver bounded authority, planning, durable effects and integrated migration
slices. Accepted design is not permission to claim unfinished runtime support.
