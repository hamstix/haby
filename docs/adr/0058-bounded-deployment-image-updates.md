# ADR-058: Bounded Application image updates of managed Deployments

- Status: Accepted
- Date: 2026-11-17
- Scope: P3 durable UPDATE sequence after issue #248
- Implementation: Not implemented by this decision; staged issues follow #248.
- Extends: ADR-033/035/045/046/047/049/050/051/052/053/054/055

## Context

Initial Deployment creation, configuration publication and readiness do not prove
safe durable updates. The conditional write kernel alone does not authorize an
UPDATE generation. The first operational update must preserve managed Resources,
external object identity and configuration access throughout rollout and retirement.
This work takes priority over Service, headless Service and individual Ingress;
those accepted P3 capabilities remain required later.

## Decisions

### 1. Applied baseline

Ordinary UPDATE starts from a fully applied generation with AppliedGeneration equal
to DesiredGeneration. An incomplete rollout uses the separate explicit corrective
path in [ADR-052](0052-corrective-generations-for-incomplete-workload-rollout.md).

### 2. Bounded image change

The first scenario changes ApplicationVersion and/or the resolved image, through
applicationVersion-derived tag, explicit tag or reference. Resource, Component and
Workload sets, exact Provider and Placement revisions, namespace, external names,
containerName and all other effective settings remain unchanged. Addition, removal,
relocation and adoption are excluded. Image resolution remains typed under ADR-054.

### 3. Existing operator pipeline

Import -> ChangeSet -> Plan -> Commit -> Execute -> Wait remains the pipeline.
The CLI communicates only with Haby; the Host performs Kubernetes calls. A future
apply command orchestrates these boundaries without merging their semantics.

### 4. Reviewable Plan

The Plan exposes old/new image, stable Deployment identity, configuration reference
changes and planned obsolete-publication retirement. Existing Resources are shown
as reuse. Accepted inputs and the prior/target inventory are frozen, not resolved
again from mutable aliases or defaults during execution.

### 5. Resource continuity

Reuse exact confirmed Resources, database, role, SecretVersions and original creation
evidence. A version bump never reprovisions Resources or rotates credentials.
Missing continuity evidence causes a typed refusal, not inferred ownership.

### 6. Explicit delta validation

Planning resolves inputs normally, then validates the allowed delta. Changed selected
Placement, Profile or other configuration inputs are refused by this bounded image
update path; they are neither silently ignored nor absorbed into the rollout.

### 7. Generation-specific configuration

Configured Workloads receive new exact ConfigurationDocumentRevisions for the new
generation, even when rendered JSON is equal. Cross-generation deduplication/reuse
is deferred. Retries within the same generation reuse its prepared results. The
exact credential sources remain unchanged.

### 8. Publish before patch

Prepare documents and authorize their exact runtime revisions before patching the
Deployment. Old publications remain available during old/new Pod coexistence.
Materialization alone does not grant delivery access.

### 9. Stable runtime identity

Reuse the stable ServiceAccount UID and runtime binding. Preserve original identity
creation proofs and record new-generation authorized-use evidence. Revoked bindings
or replaced identities are never silently resurrected.

### 10. Existing authentication boundary

[ADR-050](0050-kubernetes-runtime-identity-and-configuration-publication.md) remains
service-account-bound: an old Pod may obtain a newly published exact document if it
knows its ID. Normal clients use pinned bootstrap IDs; no latest or automatic hot
reload is introduced. Lifecycle observation does not add generation-bound auth.

### 11. Conditional update of the same object

Patch the same Deployment UID only after checking owner identity, previously
confirmed managed fields and current resourceVersion. Use narrow conditional writes;
preserve unrelated metadata and sidecars. Drift of owned fields conflicts. Never
recreate or adopt an object to make an update succeed.

### 12. Rollout strategy

Use the exact Placement Recreate/RollingUpdate settings; do not infer strategy from
Environment names. Recreate can cause downtime. RollingUpdate is not a general
availability guarantee independent of application readiness and cluster capacity.

### 13. Submission and readiness

Execute submission remains distinct from Wait. Readiness checks the target controller
generation, managed projection, probes and replica state. Old Ready evidence cannot
complete the new generation. Preserve bounded server-side batch waits; no atomic
cross-Application rollback is promised.

### 14. Uncertain writes

Persist dispatch authority before external writes. Lost responses and restart lead
to exact observation, not blind repeat. An unresolved outcome remains uncertain;
observation must establish the intended target or a typed conflict before progress.

### 15. Old-recipient evidence

Before retiring old configuration permissions, prove old recipients have finished,
including terminating Pods, using exact Deployment UID -> ReplicaSet -> Pod identity.
Labels or availableReplicas alone are insufficient. This is lifecycle evidence,
not an additional runtime authentication requirement.

### 16. Bounded retirement

Retire only obsolete runtime permissions in this first update path. Preserve the
continuing Deployment, ServiceAccount and binding. Use explicit frozen obsolete and
retained inventory plus atomic live-reference checks protecting target and other
active preparations. Retain document and history records. Static delivery grants
have an independent lifecycle. ReplicaSet history remains controller-managed.

### 17. Applied completion

Advance AppliedGeneration only after required retirement. Ready with unfinished
retirement retains the previous AppliedGeneration. Resume the unfinished stage
without reprovisioning, rotating credentials or repeating a confirmed rollout;
revalidate safety after a pause as required by ADR-051.

### 18. Forward correction

No rollback is added. Fixing an external image-pull credential can allow the same
generation to resume. A bad image requires explicit ADR-052 corrective generation,
executor fencing and preserved history, with no uncertain writes and before retirement
starts. Corrective support may be a separate implementation slice but is mandatory
before this UPDATE workflow is declared operationally ready.

### 19. Managed objects only

Only Haby-created and confirmed objects and Resources qualify. Matching names,
images or labels never authorize adoption of an existing installation.

### 20. Delivery sequence and evidence

After #248, implement sequentially:

1. Admission, Plan and Commit: prior baseline proofs, Resource reuse and exact old/new
   inventories. This slice alone does not enable runtime UPDATE.
2. Configuration preparation/publication, stable identity continuity and durable
   conditional configured UPDATE.
3. New readiness, old-recipient observation, permission retirement and Applied completion.
4. ADR-052 corrective generation for incomplete rollout.
5. Real lifecycle rehearsal covering the integrated update and correction path.

Use existing test venues and preserve mandatory checks. Prove stable Deployment UID,
database, role and credentials, changed image, exact configuration delivery, old/new
coexistence and revocation, restart and lost-response behavior. Do not introduce a
PostgreSQL version matrix or a load campaign. The separate >=150-client qualification
remains post-P4. Shared durable persistence stays Provider-neutral under ADR-035;
Kubernetes-specific intent/evidence remains typed adapter data. ADR-033 permits a
single current model but never a silent catalog reset or weakened runtime safety.

## Consequences

The first update has a deliberately small allowed delta but a complete safety
lifecycle. Equal document bytes do not remove generation provenance. Successful
submission or readiness alone is insufficient for Applied completion. This ADR
accepts architecture and ordering, not implementation evidence or P3 completion.
