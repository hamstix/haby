# Exact runtime configuration delivery

The independent ConfigurationDelivery service now has two deliberately distinct
methods on the existing Host/TLS endpoint. P2 `Deliver` retains its static scoped
token contract. `DeliverRuntime` uses online runtime authentication followed by
fresh exact publication authorization. Neither operator nor static delivery tokens
grant runtime authority. Runtime credentials grant no operator, reflection or P2
static authority.

```csharp
var reply = await client.DeliverRuntimeAsync(
    new DeliverRuntimeConfigurationRequest {
        BindingId = bindingId, RevisionId = exactRevisionId
    },
    new Metadata { { "authorization", "Bearer " + projectedCredential } },
    deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: cancellationToken);
```

Use the generated client, the exact published IDs and exactly one Authorization
Bearer metadata value. Do not put credentials in protobuf fields, URLs, command
arguments, logs or diagnostics. Configure HTTPS/trust for the existing shared
endpoint. The default loopback address is not an in-cluster endpoint. The compiled
`RuntimeIdentityConfigurationExample` shows a single call with caller-owned TLS,
deadline, cancellation and transient credential handling. It implements no token
file reader, startup retry loop, projected volume or Deployment integration.

The response contains complete sensitive JSON bytes, not the operator redacted
preview. Do not print, trace, persist, hash or compare it through diagnostic
serialization. Its revision and source fingerprint identify the saved unresolved
artifact; they are not a hash of the completed confidential response. Copy only
into the application's required in-memory configuration and release unnecessary
references. Haby clears owned material/result buffers; managed strings and gRPC
copies cannot all be guaranteed erased.

## Authorization and immutable sources

The binding selects saved installation, Environment, ManagedWorkload, Provider,
exact Provider configuration and audience. Kubernetes TokenReview must validate
the ServiceAccount username/UID, audience and Pod-bound identity. The application
ServiceAccount needs no Secret-read privilege to use this method. The configured
Haby administrator needs the existing TokenReview permission; these credentials
are distinct from delivered Resource-owned ApplicationLogin material.

Successful authentication is not document authority. A fresh coherent catalog
snapshot checks that same authenticated Environment/ManagedWorkload/Provider,
the exact binding/revision permission, receipt and recipient/source metadata.
Only then is the saved unresolved artifact loaded, checked and transiently
formatted using its frozen approved references, outputs, formatter pins, owner
and ApplicationLogin purpose. Materialization without publication is denied.
No latest lookup, replan, output regeneration, static grant creation or
AppliedGeneration advancement occurs. Exact published preApplied documents are
eligible through this runtime boundary; ordinary operator materialization and P2
grant eligibility stay unchanged. Static Profile secret projections remain
unsupported (#209).

The ServiceAccount/binding is stable across ordinary Workload generations.
An old Pod with this identity can fetch a newer exact explicitly published
revision when it knows its ID. There is no Pod-generation isolation claim.
People able to create Pods under or execute within the trusted ServiceAccount
can exercise that identity. No Pod -> ReplicaSet -> Deployment ownership check
is implied.

Revocation committed before the coherent permission decision denies delivery.
A request whose exact authority has already been accepted may finish after
revocation. Every later independent request authenticates/authorizes afresh;
there is no reusable authority or positive permission/TokenReview/material cache.
Revocation does not erase configuration already loaded by an application.
Replaying an old publication intent does not reactivate revoked access.

## Bounds and safe failures

`Haby:RuntimeDelivery:RequestTimeoutSeconds` defaults to 30 (inclusive range 1..30).
`Haby:RuntimeDelivery:MaxConcurrentRequests` defaults to 32 (1..256). Invalid
settings fail Host construction. These are startup snapshots; restart to change
them. One nonwaiting request lease covers authentication through complete reply
copy; excess requests fail ResourceExhausted without another queue. The separate
[runtime authentication scheduler](215-runtime-tokenreview-authentication.md)
continues to enforce its global/Provider TokenReview rates, concurrency and queue.

One linked request deadline covers every stage. Caller cancellation/deadline is
preserved; internal server timeout is Unavailable. No stage resets the total
deadline and no automatic server retry is added. Credentials are bounded to
16,384 ASCII bytes. Each material source is at most 64 KiB; complete escaped JSON
at most 4 MiB with 64 semantic containers; complete protobuf at most 8 MiB.
Failures return no partial configuration. Per-request bounded source/output
storage is not a demonstrated total server capacity guarantee.

| Outcome | Meaning |
| --- | --- |
| Unauthenticated | Invalid/malformed identity, IDs, credential or unsupported trust |
| PermissionDenied | No exact authority, wrong scope or revoked binding/permission |
| ResourceExhausted | Request/authentication admission or output bound |
| Unavailable | Indeterminate storage/API/internal timeout failure |
| FailedPrecondition | Authorized artifact/formatter/material is invalid or unavailable |
| Cancelled / DeadlineExceeded | Caller cancellation / elapsed caller deadline |

Unstructured TokenReview `status.error` remains Unavailable without inspecting or
returning its text. Some known invalid credentials produce this category; it
does not prove credential validity or a temporary root cause. A clean negative
identity response is Unauthenticated. Any later client retry must remain within
its original finite startup deadline; the retrying startup client is a later slice.

## Evidence boundaries

Fast tests exercise the shared core and ordered wrappers. Persistence tests use
separate DbContexts, metadata-only SQL projections and actual publication/revoke
transitions. Generated-client Host cases distinguish controlled identity
transport from real TokenReview. The existing durable API contour adds initial
static and mixed confirmed ApplicationLogin runtime RPC paths, real application
credential DDL/DML, revoke-during-TokenReview and post-authorization revoke, repeat
and Host restart. The private fixture transport changes only test CA handling;
it does not replace runtime identity or publication policy with mock success.

API-server-only k3s establishes Pod-bound API validation, not kubelet projected
file rotation, actual application startup or a managed v2 Workload rollout.
Execution results must be attributed to exact source and reported separately
from this executable inventory. Existing failed runs remain failures; no
unchanged rerun is implied. Ordinary full/Native AOT/qualification gates remain.

The >=150-client/two-document mass-start rehearsal remains pending after P4
under #217. It is not a #219 or P3 gate. Capacity at that load is unverified and
must not be advertised as demonstrated. P3 remains incomplete.
