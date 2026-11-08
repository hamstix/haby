# ADR-044: P2 rehearsal follow-ups and explicit Application materialization

- Status: Accepted
- Date: 2026-11-08
- Scope: P2 runtime evidence, operator diagnostics and explicit batch materialization
- Extends: ADR-017/018/033/035/040/041/042/043

## Context

The manual rehearsal at main b03058e confirmed the real bootstrap -> five-operation
database/login provisioning -> configuration materialization -> scoped delivery ->
application DDL/DML path, authorization separation, revocation and restart. Overall
checklist completion was partial: timeout 200/15 was observed in frozen Plan inputs
but not asserted in the receiving application's in-memory connection settings.
This is missing evidence, not an observed rendering defect. The local external
report is operator evidence, not a portable repository artifact or production
readiness claim; it contains environment inventory and must not be copied wholesale.

## Decision

### Ordered follow-up slices

1. Add safe receiving-client assertions for timeout, commandTimeout and sslMode,
   and correct misleading diagnostics for the admitted operation graph and Resource
   dependencies. Preserve the existing actual application connection/DDL/DML probe.
2. Add Profile discovery: list Profile identities with their latest imported
   revision, and list imported revisions for an exact Profile identity. Discovery
   must not introduce implicit latest references into assignments or Plans.
3. Add explicit materialization of all documents of an Application's Components
   through one operator command and one server request.

These are sequential focused slices. This ADR does not implement them or authorize
combining Profile discovery and batch orchestration into the first change.

### Receiving-client evidence and truthful diagnostics

Assert settings from the JSON actually obtained through authorized delivery and
loaded into in-memory application configuration, using the driver's typed parsed
settings. api timeout 200 and worker timeout 15 must have independent expected
values. Also check commandTimeout and sslMode, with negative mismatch evidence.
Do not compare a Plan to itself or print the connection string, password, resolved
JSON or verifier. Keep PostgreSQL settings and Npgsql interpretation in the example/
adapter boundary; the independent transport client remains Provider-neutral.
Ordinary example invocation remains useful without optional rehearsal assertions.

The supported five-node Plan must not be presented as unsupported solely because
an older single-action description cannot represent it. Distinguish the supported
graph from availability of detailed action descriptions. Do not fabricate an
action, silently suppress a real incompatibility, or treat description metadata as
authority to execute. Preserve exact frozen inputs and typed unavailable failures.

Resource-use diagnostics must account for explicitly recognized dependencies,
including the admitted application-login databaseRef, without losing genuine
unused-Resource warnings. Provider-specific dependency interpretation belongs to
registered exact contracts/adapters; common code must not scan arbitrary properties
for fields named databaseRef or switch on PostgreSQL contract names. Do not make
import depend on Environment, a live Provider or credential access. Unknown opaque
configuration does not become a generic reference discovery language. Persisted
historical warnings remain unchanged; corrections affect new validation/import.

### Explicit materialization of an Application

The explicit batch command selects an Application by operator names and optional
explicit generation, with ADR-041's exact internal identities. Resolve the target
generation once on the server, then enumerate the exact Component/document set
from that generation's frozen layout. Send one request; do not implement a CLI
loop that independently selects the current generation for each document.

Each document retains its separate atomic acceptance and immutable identity.
Return per-document outcomes and revision IDs; do not promise a transaction across
all documents. A partial failure does not roll back previously accepted documents.
A repeat reuses already accepted revisions and processes remaining documents under
their existing exact-key idempotency and eligibility rules. It does not overwrite
artifacts or relax the requirement for an eligible fully applied generation.

Materialization remains an explicit step, never an automatic consequence of
generation execute. Configuration preparation failure must not retrospectively
change successful provisioning or AppliedGeneration. It performs no new Resource
effects, secret resolution, access issuance or Workload deployment. Detailed wire
shape, bounded result limits and interruption/reporting behavior must be specified
when preparing that slice; this decision does not invent a batch operation journal.

### Deferred access issuance and rollout

Defer batch delivery-grant issuance. Existing explicit exact-document grant creation
remains supported. No shared all-Component token, automatic credentials at execute,
or automatic grant publication is introduced by batch materialization.

Revisit automatic issuance with Workload credential distribution, including intended
recipients, durable secure transfer, rotation/revocation and partial failure. A
future release pipeline can compose import, planning, Resource execution,
materialization, credential distribution and Workload rollout, but that complete
multi-Application automation is not part of the present operator slice. Kubernetes
Secret/volume mechanics must not enter common materialization contracts.

## Evidence and boundaries

The first slice requires focused regression tests before diagnostic fixes, safe
client mismatch checks and the real authorized Host/PostgreSQL receiving path.
Preserve complete solution, PostgreSQL, Native AOT and dependency gates. Distinguish
the existing create/insert/read probe from a full EF migration rehearsal. Do not
reset catalogs, rewrite historical warnings or expose credentials to close an
evidence gap. New diagnostics/presentation remain current alpha contracts under
ADR-033; Provider-neutral persistence and generated serialization remain mandatory.

## Application materialization implementation (#156)

`habyctl configuration materialize <application-id> --all` sends one authenticated
generated operator RPC, with existing Environment/context selection and optional
positive `--generation`. Single-document flags are mutually exclusive with `--all`.
The Provider-neutral Application use case selects one exact eligible frozen inventory,
orders ComponentId/DocumentId ordinally, preflights count/response metadata and then
materializes sequentially through the existing exact per-document port.

The first failure stops processing; successful revisions and original audit survive,
and the rest of the complete inventory is NotAttempted. An eligible empty inventory
succeeds without writes. The top-level status distinguishes complete success from
failure. There is no batch transaction or durable journal. Cancellation/response loss
may hide accepted documents; only explicit same-generation retry is an exact retry.
Concurrent calls retain the existing immutable per-document convergence rules.

The finite limit is 512 items, with a deterministic pre-write reservation of 16 KiB
plus 12 KiB per item within the existing scoped 8 MiB budgets. Bounded metadata and
complete CLI validation prevent truncated/foreign/malformed ordinary output. See
[runbook 146](../development/146-configuration-materialization-preview.md#explicit-application-materialization)
for wire outcomes, metadata budget proof, retry syntax and test evidence. No catalog
migration/reset or generation-update admission is introduced. Automatic materialization,
batch grants, secret resolution and Workload integration remain deferred.
