# Initial internal Workload-only generations

Issue #206 extends the temporary internal Plan/Commit/CREATE composition from
[the mixed-generation path](202-durable-workload-intents.md). A genuinely imported
Application with at least one Workload and zero Resources can start at generation
1 with ExpectedAbsence. No prior deployment, PostgreSQL Resource Provider, dummy
Resource, generated credential or applied baseline is required. PostgreSQL remains
the catalog. This path has no Host/RPC/CLI entry point or public effects switch.

## Accepted inputs and bounded admission

The same ChangeSet, Plan, generation and Commit stores capture the complete
normalized imported declaration, configuration, exact Environment-owned Provider
revisions and frozen Placement revisions. The full v4 fingerprint includes graph
and configuration bytes. Internal input pins retain referenced administrative
revisions. Commit holds the existing aggregate/alias/Placement locks and checks
ExpectedAbsence and freshness atomically. Competing initial ChangeSets yield one
accepted deployment/generation; equivalent Commit replay returns its original
receipt, including timestamp precision.

Pure exact handler admission checks every declared Workload before desired
acceptance. The adapter resolves the full frozen manifest, retaining name collision
checks, then validates every effective Deployment through the existing bounded
intent preparation. Unsupported later subjects cannot be silently ignored. Missing
exact handler or codec support refuses. No secret resolution or Kubernetes I/O
occurs in those catalog transactions. Action execution still checks current scoped
administrative secret authority through the existing client boundary.

Supported fields are the existing Deployment kernel subset: explicit/composed image,
stable main container name, supported replicas/strategy/rollout settings,
nodeSelector and references to existing imagePullSecrets. Effective env, ports,
probes, volumes/mounts, resources, routes, configuration documents and companion
objects remain unsupported. Bindings, Component documents, Parameters and Exports
are not admitted by this bounded path. Multiple supported Workloads retain the
existing full-generation resolver and exact per-subject intent semantics.

## Empty ResourcesReady is evidence

The existing graph format 3 stores an explicit empty Nodes array, omits empty
optional Reuse/Retained arrays and has no Baseline. It is a structural Resource-stage view, not permission to
execute. The original full declaration remains frozen separately; stripping
Workloads in the Resource projection does not grant empty-stage authority.

The existing checkpoint evidence format 1 explicitly stores an empty Resources
array, UUID, exact deployment/generation, accepted full fingerprint and confirmation
time. Its hash and immutable row are checked on reads. No successful attempt or
output is invented. A shared catalog authority validator binds it to the actual
imported full manifest, exact accepted Plan/ChangeSet/Commit bytes and receipt,
complete administrative pins and initial generation-1 ExpectedAbsence/null-baseline/
null-Applied state. All Resource declaration, graph, reuse, retention and management
obligations must be absent. Nonempty, corrupt, foreign or mismatched evidence
refuses. Missing proof after action acceptance cannot be regenerated or repaired.

The ordinary stage writer persists the checkpoint under the existing deployment
lock and unique stage key. Action acceptance references that actual checkpoint;
request and subject/action uniqueness retain stable intent identity. Reads and
restart loading repeat accepted identity and proof checks. The existing mixed
path keeps its nonempty Resource reuse and original fully applied provenance.
Public Resource-only admission, completion, execution and recovery remain closed
to this internal scope.

## Completion and validation boundaries

Empty ResourcesReady only establishes that this accepted declaration requires no
Resource effects. Internal CREATE confirmation records the actual Deployment UID,
operation marker, attempt and receipt. It establishes neither readiness nor public
staged submission completion. AppliedGeneration remains null for the initial path
and stays at the previous actual applied generation for the mixed path.

After a committed marker, interruption or lost response permits observation only.
Restart and current-build catalog restore retain the same empty proof and intent.
Exact owner/operation/projection observation can confirm the original CREATE while
retaining the original Uncertain attempt. Absence, foreign evidence or an unavailable
API does not authorize adoption or another write. Terminal replay returns the
saved receipt without contacting Kubernetes.

Persistence tests cover initial acceptance, concurrency/idempotency, public
shortcuts, whole-shape refusal, exact support, stale/frozen selection, retention,
scoped secret refusal and damaged authority. The existing durable launcher adds
initial direct CREATE, replay, response loss, restore/restart observation and
marker-before-send absence. It retains all existing mixed/P2 cases and owned cleanup.
Run it with the command in the [durable guide](202-durable-workload-intents.md).
Executed results and limitations belong to exact-source task/CI evidence; this
guide does not assert those gates have passed or grant merge authorization.
