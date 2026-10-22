# ADR-004: Operator API transport strategy

- Status: Accepted
- Date: 2026-10-21

## Context

The PostgreSQL architecture checkpoint uses a private gRPC service in its
integration-test assembly. The shadow pilot needs an operator-facing path for
real imports and inspection through `habyctl`, but it does not need the stable
public REST and gRPC contracts planned for M4. The first operator API must stay
small and replaceable while Haby is validated in an isolated environment.

Haby continues to run on CoreCLR. Native AOT is a direction, not a release gate
for the server or EF Core persistence under ADR-003.

## Decision

### Start with an internal alpha gRPC API and CLI

The first deployable operator API uses ASP.NET Core gRPC. `habyctl` is its first
client and uses generated client types. The initial shadow-pilot surface is
limited to manifest validation, import and read/inspection of accepted
Application and ManifestRevision state. Planning may be added only when its
non-mutating shadow semantics are specified and implemented. Desired-state
commit and Provider execution are not exposed in the shadow workflow.

The API is an internal alpha contract: protobuf package names, messages and
methods may change incompatibly or be replaced in full before M4. Do not
publish it as a stable OSS integration contract or promise client compatibility
across alpha updates. Its implementation adapts the existing transport-neutral
application use cases; gRPC-generated types do not enter Domain or Application.

### Shadow preview boundary

A shadow preview evaluates one immutable accepted ManifestRevision against an
explicit Environment ID and exact, non-secret Provider alias selections supplied
with that request. This preview context is hypothetical and request-scoped: it
is not an authoritative persisted Environment or Provider configuration. The
preview reuses the checkpoint's alias and capability planning rules and fails
closed for declarations outside its single-database shape. It creates no durable
ChangeSet, DeploymentPlan, ApplicationDeployment or generation, and exposes no
preview ID or fingerprint that could be reused as an apply capability.

The later active pilot must obtain server-owned, exact, durable planning inputs
and separately accept a DeploymentPlan under ADR-016 and ADR-018 before desired
state can be committed. A shadow preview is never that Plan, even when its
hypothetical inputs happen to match later server state.

The checkpoint's private test `.proto` is not promoted verbatim. Define a
separate operator contract with typed, bounded outcomes and diagnostics.
Expected validation, import-equivalence and version-conflict results remain
ordinary typed application outcomes rather than infrastructure exceptions.
Import carries the original UTF-8 JSON envelope as protobuf `bytes` so the
shared parser can still detect duplicate properties and encoding failures.
The host enforces a finite request-size limit before passing bytes to that
parser. Transport errors, cancellation and infrastructure failures are mapped
separately from expected application outcomes.

### Keep REST deferred and transport-neutral

Do not add a parallel operator REST API merely for the first pilot. A future
REST adapter uses ASP.NET Core Minimal APIs and explicit transport DTOs over
the same application use cases. Its JSON types use `System.Text.Json`
source-generated metadata so the adapter does not introduce avoidable
reflection-based serialization. REST and gRPC compatibility policy, public
versioning and published clients remain M4 work. JSON transcoding is not an
implicit substitute for a designed REST contract.

### Do not fix the future UI hosting model yet

A separately hosted browser UI cannot call ordinary HTTP/2 gRPC directly. If
the future UI is a browser client of this API, it may use gRPC-Web with its
streaming and deployment limitations. A server-hosted UI may call the
Application layer in-process when appropriate. Either form must preserve the
same use-case semantics; choosing its hosting and transport is not a condition
for shipping the first `habyctl` pilot.

### Treat AOT and alpha security explicitly

gRPC itself does not require choosing REST first for a future Native AOT path.
The full server distribution is not claimed to be AOT-compatible while its
persistence and other dependencies remain unproven. Neither the alpha server
nor `habyctl` requires AOT publication in the early pilots.

The early alpha API has no application-level authentication or authorization.
It may be deployed only behind the strictly limited network boundary in the
ROADMAP: one instance, no public or broadly reachable endpoint, and access
only from the explicitly permitted developer console. Transport security and
request limits remain required. Network isolation is a temporary deployment
condition, not a substitute for the M4 security model. If that boundary cannot
be enforced, do not expose the alpha API.

Without authentication, a client-provided actor name is not a trusted identity.
The server supplies an explicit technical actor identifier for import audit
metadata and must not present it as the identity of a particular developer.
Later authenticated identities can replace that alpha attribution policy.

## Consequences

- The first real client can exercise durable import without implementing both
  API transports or a browser UI.
- The initial protobuf contract can evolve quickly without a public stability
  promise, while application results remain transport-neutral.
- The operator API is not suitable for broad exposure or customer automation
  until authentication, authorization and stable public contracts are designed.
- Browser access, REST and Native AOT publication remain explicit later work
  rather than accidental commitments of the first pilot.

## Follow-up work

- After ADR-023's durable catalog slice, add a narrow alpha gRPC host and
  `habyctl` commands for validation, import and revision inspection.
- Verify the actual network restriction, transport configuration and request
  limit in the pilot deployment before exposing the endpoint.
- Revisit UI hosting, REST and public contract compatibility in M4/M5.

## Platform references

- [gRPC and Native AOT](https://learn.microsoft.com/en-us/aspnet/core/grpc/native-aot?view=aspnetcore-10.0)
- [gRPC-Web in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb?view=aspnetcore-10.0)
- [ASP.NET Core support for Native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)
