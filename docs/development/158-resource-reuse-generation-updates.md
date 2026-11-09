# Bounded Application generation updates

Issue #158 implements the first [ADR-045](../adr/0045-application-generation-updates-and-resource-continuity.md)
slice. One fully applied Application can accept a new ManifestRevision using the
same database/application-login pair, exact Resource keys, contracts, properties,
dependency and exact resolved Provider instance/configuration revision/implementation.
Issue #162 now permits changed Component/local Binding sets over unchanged managed Resources; see the [target-layout operator sequence](162-component-binding-generation-updates.md). Supported document
configuration and consumer options may change. A new ApplicationVersion with an
otherwise identical declaration is still a new target revision.

The [#164 retained lifecycle](164-retained-resource-reintroduction.md) additionally admits removal and compatible reintroduction of that standalone database while keeping the pair declared. Beyond these boundaries, Resource additions/removals/renames, Provider
rebinding, rotation, ownership-policy changes, same-revision configuration-only
updates and Workloads remain unsupported. They fail during planning; no implicit
adoption, replacement, cleanup, privilege replay or drift repair occurs.

## Operator sequence

Use an authenticated operator context prepared under [runbook 150](150-operator-access-tokens.md).
Initial installation keeps `--expected-absence` and the existing standalone or
five-step path. After generation 1 is fully applied, import a v2 envelope with the
same Resources and, for example, a changed permitted Binding timeout:

```text
habyctl manifest import application-v2.json
habyctl changeset create <application-record-id> <revision-number> --expected-generation 1
habyctl plan create <changeset-id>
habyctl plan get <plan-id>
habyctl plan check <plan-id>
habyctl changeset commit <changeset-id> --plan <plan-id>
habyctl deployment generation get <deployment-id> 2
habyctl deployment generation execute <deployment-id> 2
habyctl configuration materialize <application-id> --all --generation 2
habyctl configuration access create <application-id> --component api --document settings --generation 2 --token-output-file v2-api.token
```

The generation precondition is mandatory, positive and mutually exclusive with
expected absence. Review the explicit Reuse entries and their management/original
source identities. A reuse-only Plan has zero external operations. Commit accepts
DesiredGeneration=2 with AppliedGeneration=1; explicit Execute verifies all reuse
requirements and then advances AppliedGeneration. It creates no operation,
attempt, dispatch marker, password version or copied output. No materialization,
delivery grant or rollout happens automatically. Use expected generation 2 for v3.

Materialization uses the target generation's frozen configuration and direct
original successful outputs. Each new document has its own target-generation
identity, including when content matches an older document. Issue each new grant
explicitly and transfer it through the existing sensitive-output workflow. Earlier
documents/grants keep their exact original configuration until expiry/revocation;
they never follow latest. Receipt does not imply hot reload of a running process.

## Freshness, refusal and retries

- Only DesiredGeneration=AppliedGeneration can be an update baseline. Resolve an
  incomplete/NeedsAttention generation through its existing supported procedures;
  another update cannot supersede it.
- A wrong expected desired generation is `changeSetStale`, requiring new intent.
  A changed alias/Profile input is `planStale`, requiring explicit replanning.
  A selected different Provider revision is unsupported for existing Resources.
- Missing/corrupt/foreign management or confirmed evidence is unavailable, never
  proof of external absence or permission to regenerate. Manual database recovery
  without confirmed original output cannot authorize pair reuse.
  Original action/output support must remain registered; a missing validator never
  turns a lost mandatory output into an optional one.
- Exact already accepted effective state is a successful no-op at the current
  generation. A same-revision configuration-only change is refused, not silently
  accepted as a no-op. No-op does not create a generation or rewrite its audit.
- Exact Commit replay returns its original receipt even after later generations.
  Execute repeat validates retained evidence and performs no external work.
  Cancellation before acceptance rolls back; a confirmed commit retains its
  receipt even if cancellation/transport loss hides the response. Inspect/retry the
  exact ChangeSet/Plan or generation instead of guessing a new generation.

## Catalog and adapter boundaries

Migration `ReuseManagedResources` adds nullable expected-generation intent,
Provider-neutral `resource_management` and positive/ordered generation constraints.
Commit-to-generation becomes many-to-one to retain explicit no-op receipts.
Initial acceptance creates each stable management identity, referencing its exact
original accepted generation. A reuse graph freezes the baseline plus direct
original source/evidence fingerprints; v3 does not recursively traverse v2.

The migration is additive for the currently admitted pre-update catalog. It does
not backfill management records. **Existing installations without these records
cannot update their Resources.** Reads, old documents/grants and existing initial
execution/recovery retain their behavior. A new disposable rehearsal can use an
explicit fresh catalog; resetting a catalog never removes external objects and
does not authorize adopting them. Startup never resets data or manufactures
historical success/ownership. Current-build restart and dump/restore keep exact
management, attempts, outputs, slots, documents, grants and audit.

Common code handles stable owners, original producer/attempt/output identity,
slot/purpose metadata, canonical evidence and atomic acceptance. Registered
adapters own compatibility and typed output/configuration interpretation.
`ValidateReuseAsync` is a metadata-only validator: no Provider calls or material
resolution. Plan/Commit inspect material availability metadata; Execute checks the
saved generated material without creating or replacing it. A synthetic queue
family exercises the same shared reuse path without PostgreSQL contract branches.

## Evidence

`GenerationResourceReuseTests` runs authenticated actual Host/generated RPCs and,
when required by the existing native gate, the published Native AOT CLI. It proves
v1 DDL/DML -> v2 -> v3, unchanged OIDs/owner/credential and operation history,
explicit zero-operation execution, all-document materialization, typed received
settings, continued old grants, no-op/repeats, restart before Execute and real
pg_dump/pg_restore. The restarted update Host has no stored Provider administrator
material, independently preventing new Provider effects through that configuration.

The safety/precondition tests cover actual PostgreSQL lock-observed competing
commits, cancellation around acceptance, unsupported declarations, stale exact
inputs and unavailable/corrupt/foreign original evidence. Synthetic tests exercise
the shared boundary. These are automated disposable-contour checks, not a claim of
general production readiness, arbitrary Resource evolution or manual acceptance.
Full-suite timing/artifact gates remain those of [issue 140](140-postgresql-integration-performance.md).

## Mixed reuse and create (#160)

Initial installation still admits only a standalone database or the exact
database/login pair. An initial pair plus independent databases is refused during
adapter graph validation (`planInputUnavailable`), before a Plan or desired
generation is accepted and before any Provider effect. Install/apply the pair
first, then use the additive update below; pair creation execution is not expanded.

After the original pair is fully applied at generation 1, import a new version
retaining every Resource, Component and Binding identity. Add one unused Resource
(for example `extra`) with type `postgresql.database/v1alpha1`, the **same exact
Provider alias/configuration revision**, and properties `{ "name": "sample_extra" }`.
Use a new independent physical name. Do not add a Binding, login, access profile,
owner/restrict dependency or renderer for this database. It has ordinary standalone
CREATE semantics; no additional PUBLIC/owner/access policy is inferred.

Run the operator sequence above with expected generation 1. Review **two Reuse
entries plus one `extra-create` operation**. Plan and Commit do not contact the
Provider or create the database. Commit atomically accepts Desired=2, Applied=1,
retains the original pair's management IDs/source=1 and creates only the new
management record (source=2) and Pending operation. A management record alone is
not successful ownership/effect evidence.

Exact operation get/execute now select positive committed generations as well;
an absent generation or operation returns NotFound. Recovery still requires its
existing action-specific eligibility, not merely a valid operation key.

Explicit generation Execute validates every reuse requirement before new dispatch.
The new operation uses its ordinary exact adapter, dispatch marker, confirmed
output and attributable attempt. Final completion locks the aggregate/continuity
records and checks both the complete operation inventory/evidence and reuse.
Applied advances to 2 only after that combined decision. If reuse becomes invalid
after the new CREATE, the confirmed new effect remains Succeeded with Applied=1;
restoring valid inputs and explicitly repeating Execute skips that confirmed effect.
This is not a product drift-repair or metadata-repair command. Completed external
nodes alone do not produce successful generation output or exit 0.

An external name collision fails without adoption or changes to the old pair.
Duplicate declared physical names and unsupported additions/mutations refuse.
Uncertainty after dispatch leaves Dispatching/NeedsAttention and blocks another
generation. There is no automatic replay, adoption, compensation or cleanup.
Existing manual database assignment is still limited to a singleton graph with
**no reused members**; it does not recover a database added in a mixed generation.
A crash before aggregate completion may leave all new effects confirmed but the
aggregate unapplied. Inspect and explicitly Execute that exact generation again.

For v3 (and v4), retain all three Resource definitions, import the new version and
use expected generation 2 (then 3). Plan contains zero operations and three direct
Reuse references: original pair source=1, extra source=2. A valid creation source
may itself contain reused peers. The reader checks the source's completed applied
position, frozen declaration/graph, own successful nodes/attempts/typed outputs and
exact owner/Provider references. It never recursively walks prior generations or
copies historical outputs/attempts. Common code enforces identity and evidence;
adapters own compatibility, dependencies and supported shape admission. A narrow
typed synthetic queue addition traverses this same persisted path.

After Execute, explicitly materialize generation 2/3 using `--all --generation n`
and issue grants as before. api/worker keep the original credential/SecretRef;
the unused extra database needs no renderer. Frozen target consumer settings may
change as in the reuse-only slice. Every previously issued exact document/grant
continues to return its original configuration. No automatic materialization,
grant creation, hot reload or Workload rollout is introduced. Per-document batch
atomicity, failure prefixes, bounds and timing artifacts remain unchanged.

This slice adds no schema migration or payload fields: the strict current graph
codec now permits disjoint reuse and creation inventories together. Existing
initial/reuse-only graph bytes, fingerprints, documents and credential histories
are not rewritten. Catalogs admitted after #158 need no reset; its no-backfill
restriction for older catalogs still applies. Startup never resets or adopts.

Admission remains one Application, a fully applied sequential baseline, exact
ExpectedGeneration and one pinned Provider revision/implementation. One independent
standalone database may be added per update alongside the managed pair (and any
previously admitted standalone databases). New login/pair shapes, dependencies on
reused outputs, broader removal/rename/reintroduction, Provider rebinding, rotation, configuration-only intent and Workloads stay refused.
General additions and the broader P2 update checkpoint remain incomplete.

`MixedResourceGenerationTests` covers authenticated actual Host/generated clients,
published Native AOT CLI when required, independent OIDs/data/credential/history,
v1 -> mixed v2 -> reuse v3/v4, retained exact grants, restart before Execute and
real pg_dump/pg_restore. `MixedResourceSafetyTests` checks corrupt/foreign reuse
before dispatch, changed final reuse, lost success without replay, new-producer
evidence corruption, external conflict and overlapping execution/canceled waiters.
The existing lock-observed competing Commit and acceptance-cancellation tests run
both reuse-only and mixed cases. Synthetic typed-family tests run both paths.
These automated disposable-contour checks do not claim manual production acceptance.
