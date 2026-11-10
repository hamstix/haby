# ADR-049: Kubernetes object ownership, conditional updates and uncertain writes

- Status: Accepted
- Date: 2026-11-10
- Scope: Third P3 Workload decision packet; design acceptance, not implementation
- Extends: ADR-026/032/033/035/045/046/047/048

## Context

P3 must distinguish management of an external object, authority over its fields,
submission and readiness. A lost API response must not cause blind replay or
implicit adoption. Kubernetes-specific identity, patch rules and observation
belong in the adapter; common durable execution retains neutral identities and
versioned evidence. This packet does not implement Workloads or change P2 behavior.

## Decision

### 1. Object ownership and field ownership differ

Haby manages a specific object created for a specific ManagedWorkload. It writes
only fields explicitly owned under the closed adapter contract. Unrelated metadata
or injected fields are not discarded merely because the object belongs to Haby.

### 2. Exact external identity

Save the exact Provider/cluster binding, namespace, API group and kind, object name
and Kubernetes UID. UID distinguishes the original object from a replacement with
the same name. Store this information in typed versioned adapter payloads, not
Kubernetes-specific columns or client types in shared persistence/core contracts.

### 3. Stable readable names and explicit override

The default naming rule is:

```text
baseName = applicationId-componentId-workloadId

Deployment         -> baseName
Service            -> baseName
Headless Service   -> baseName-headless
Individual Ingress -> baseName
```

Always include all three logical identifiers, regardless of Component/Workload
counts or declaration order. No special main name, primary link, mandatory hash
suffix, automatic truncation or random disambiguation is introduced.

An optional nameOverride replaces baseName completely. The headless suffix still
applies to that base. Use the override for exceptional names and future migration
preparation; choosing a name confers no authority to adopt an existing object.

Validate final names against each corresponding kind's constraints, including the
headless suffix. Detect collisions in the actual cluster/namespace/group/kind/name
scope and refuse them explicitly. Equal names of different kinds in the mapping
above are intentional, not a collision. Separate installations have no shared
reservation or implicit external ownership merely because their names coincide.

Image/configuration/replica/generation changes preserve names. Changes to identity
or nameOverride that produce another external name for an already managed object
refuse as unsupported renaming. Moving to another namespace/cluster is likewise
not an initial in-place update capability. No automatic adoption, ownership transfer
or delete-and-create replacement is implied.

### 4. Metadata and ownership evidence

Use Haby-owned labels/annotations for stable owner identity, managed-object identity,
write-operation identity and target generation. They contain no credentials.
After confirmed creation, saved UID plus ownership is authoritative for continuing
management. Name, ordinary labels or equal object content alone never prove it.
The concrete metadata keys and their closed payload/version must be specified in
the implementation contract; they are not an arbitrary user annotation surface.

### 5. Foreign objects refuse

An occupied creation name without confirmed ownership is a conflict. Initially do
not adopt, replace, delete or force ownership of that object. Registering existing
objects requires a separate explicit migration/adoption decision.

### 6. Explicit top-level inventory

Track Deployment, ordinary Service, headless Service and Ingress individually.
The Deployment controller manages its ReplicaSets and Pods; Haby initially does
not update them as independently managed objects. References to existing Secret,
ConfigMap, PVC or imagePullSecrets are not Haby-owned inventory or delete authority.

### 7. Conditional create and update

Create through a named object creation request. Update through a narrowly scoped
JSON Patch, not whole-object replacement or generic JSON merge. Before updating,
verify UID, ownership and expected managed-field state. The write itself tests the
UID and just-read resourceVersion, protecting the read-to-write interval.
Kubernetes supports test conditions in JSON Patch for such conditional writes:
[API patch operations](https://kubernetes.io/docs/reference/using-api/api-concepts/#patch-operations).

### 8. Closed managed-field contract

Define owned fields explicitly: for Deployment, these include stable selector,
replicas/strategy, the supported main-container settings, declared probes, volumes/
mounts and Haby service metadata. Handle keyed lists according to Kubernetes
semantics, preserving entries outside Haby's supported ownership. Do not replace
all containers or metadata just to change an image.

Immutable field changes such as a Deployment selector initially refuse instead of
silently deleting and recreating. Detail removal/omission/defaulting behavior and
ownership granularity per supported field before implementation. Unsupported shapes
refuse; field preservation must not become unbounded PodSpec passthrough.

### 9. Manual managed-field edits conflict

If confirmed image v1 was manually changed to hotfix, a new target v2 must not
silently overwrite it. Return a clear managed-field conflict. An operator may
restore the confirmed state and explicitly resume. Force override or adoption of
external edits needs a later accepted capability. An unrelated annotation edit
is not such a semantic conflict.

### 10. resourceVersion is a write precondition, not desired state

Status or permitted external edits can change resourceVersion. Read it immediately
before conditional writing rather than freezing it as a semantic Plan input.
After a confirmed precondition rejection, the adapter may boundedly reread and
revalidate ownership/managed fields. Continue only while all safety conditions hold.
This is retry after a confirmed rejected write, never replay of an uncertain effect.
Concrete retry/time budgets belong to the executable action contract.

### 11. Submission is not readiness

Confirmed writing means Kubernetes accepted the intended object state, not that
Pods are running or ready. Save UID, confirmed managed projection and observed
Kubernetes generation. Advance AppliedGeneration only after the accepted readiness
criterion under ADR-046. Not waiting never waives that requirement.

### 12. Save write intent before dispatch

Persist exact target/action, expected UID for updates, target managed values,
unique operation identity and necessary initial conditions before sending a write.
Write the operation marker atomically with its managed-field changes.
A top-level operational annotation must not itself alter the Pod template or cause
an unnecessary rollout. Application configuration/Pod-template revision markers
have their own explicit semantics; they must not be accidental write-attempt IDs.

### 13. Observe after a lost response

Timeout, broken transport or restart after dispatch triggers observation before
any further write. Automatically establish the result only when ownership, expected
UID for updates, exact operation marker and target managed-state projection agree.

For first creation the UID is not known yet. It can be recorded from an object
matching the exact pre-persisted creation operation identity, owner and intended
state. This is action-specific evidence recovery, not same-name adoption.

This operates within the accepted trust boundary for cluster administrators.
Markers do not defend against a privileged administrator deliberately copying
ownership/operation metadata. Do not treat them as cryptographic credentials.

### 14. Insufficient evidence remains uncertain

After response loss, missing object, unexpected UID, missing/foreign operation
marker or mismatched managed state cannot be treated as success or overwritten
automatically. An unavailable API permits subsequent observation attempts, not
write replay. A single NotFound after uncertain creation does not prove that the
write never happened and initially requires attention.

Record confirmation by observation distinctly in history; never fabricate an HTTP
success response or a new successful write attempt. Retain original uncertainty
and the exact evidence supporting any eventual resolution. This does not reuse
PostgreSQL manual database-assignment semantics for Kubernetes actions.

### 15. Missing managed objects do not authorize recreation

If an already managed object disappears, stop its update and report absence.
A different UID under the same name is an identity conflict. Automatic repair of
missing objects requires a later explicit reconciliation policy.

### 16. Addressed, conditional deletion

Future cleanup deletes only inventory objects with matching ownership and UID.
Do not bulk-delete by label selector, strip foreign finalizers or delete referenced
Secret/ConfigMap/PVC objects. DELETE acceptance is not completed deletion; observe
its outcome. Exact retirement/access-release stages need the subsequent cleanup
packet. This decision does not implement or waive required cleanup.

### 17. No incidental companion-object cascade

Initially keep Service/Ingress retirement in explicit durable Haby cleanup instead
of assigning their ownerReferences to Deployment. Manual Deployment deletion must
not implicitly remove routing before Haby evaluates the state. Preserve ordinary
Kubernetes-managed Deployment -> ReplicaSet -> Pod dependencies.
See [owners and dependents](https://kubernetes.io/docs/concepts/overview/working-with-objects/owners-dependents/).

### 18. Real API evidence

Prove normal create/update with stable UID, foreign occupied name, managed-field
conflict, preservation of unrelated fields, read/write races, response loss after
accepted writes, restart before confirmation, absence/replacement UID and failures
without false submission/readiness success. Include names/overrides/derived-name
length and collision cases from section 3. Use a real test API server/cluster;
client mocks alone do not prove conditional update or Kubernetes defaulting behavior.

## Remaining contracts and implementation boundaries

Agree exact managed-field normalization/defaulting, keyed-list patch behavior,
metadata keys and evidence codecs, bounded observation/retry/error outcomes and
supported Kubernetes API versions before implementing their slices. Secret-bearing
bodies must never become ordinary snapshots, diagnostic values or secret-derived
hashes. Runtime configuration/credential transfer, readiness/wait and mandatory
retirement cleanup are separate next packets.

The common durable framework records neutral action state/evidence. Kubernetes UID,
patch mechanics and API interpretation remain adapter-owned. Keep current alpha
rules, source-generated metadata, C#/EF persistence and all applicable existing
PostgreSQL/authentication/Native AOT gates. No Workload code or rollout is authorized
merely by documenting this packet.
