# Bounded Application generation updates

Issue #158 implements the first [ADR-045](../adr/0045-application-generation-updates-and-resource-continuity.md)
slice. One fully applied Application can accept a new ManifestRevision using the
same database/application-login pair, exact Resource keys, contracts, properties,
dependency and exact resolved Provider instance/configuration revision/implementation.
The Component and Binding identity sets must stay unchanged. Supported document
configuration and consumer options may change. A new ApplicationVersion with an
otherwise identical declaration is still a new target revision.

Resource additions/removals/renames, Component/Binding identity changes, Provider
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
