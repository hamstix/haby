# Component and Binding generation updates

The subsequent [#168 independent-pair slice](168-independent-database-login-pair.md) also admits one new database/application-login pair per update; its Resource-scoped producer and alpha catalog boundaries supersede the earlier new-pair refusal below.

Issue #162 implements the next bounded [ADR-045](../adr/0045-application-generation-updates-and-resource-continuity.md)
P2 slice. A new ManifestRevision may add/remove Components and local Bindings or
retarget a Binding to a supported existing Resource. Resource keys, contracts,
properties, dependencies, ownership and the exact resolved Provider revision and
implementation must still satisfy the existing adapter reuse checks. A Binding
cannot enable an unsupported Resource/output/renderer shape. In the PostgreSQL
pair profile it still consumes the existing application-login, not the database
or an independent standalone database.

## Prepare the target layout

1. Import the exact new ApplicationVersion first. For example, keep `db` and
   `app-login`, remove `worker`, add `scheduler` with a local Binding to
   `app-login` and `configurationSlot: main-database`, and set its permitted
   `options: { "timeout": 240 }`. The retained `api` may remove old Bindings and
   add a differently named Binding using that same slot/Resource.
2. Import any new exact Profile revisions and explicitly apply desired
   Environment/Application selections. New Components inherit those defaults
   under Environment -> Application -> Component whole-Profile precedence.
   Adjust required documents, slots and projections together: a requested slot
   without a selected rule, incompatible renderer/output, duplicate slot,
   duplicate document or colliding path refuses. Unrequested Profile rules remain
   unused under the existing contract; no new required-Binding rule is invented.
3. Optionally apply a Component exception with the explicit imported version:

   ```text
   habyctl profile assignment apply scheduler-assignment.json --application-version v2
   ```

   Assignment targets remain logical ApplicationId/ComponentId. An existing
   same-ID assignment resumes when that Component returns; a renamed Component
   inherits defaults and receives no assignment transfer. Removed assignments
   remain stored and readable. Their checked version is audit, not applicability.
4. Create and review an exact Plan, then accept and execute explicitly:

   ```text
   habyctl changeset create <application-record-id> <v2-revision-number> --expected-generation 1
   habyctl plan create <changeset-id>
   habyctl plan check <plan-id>
   habyctl changeset commit <changeset-id> --plan <plan-id>
   habyctl deployment generation execute <deployment-id> 2
   habyctl configuration materialize <application-id> --all --generation 2
   habyctl configuration access create <application-id> --component scheduler --document settings --generation 2 --token-output-file scheduler-v2.token
   ```

   Use an [authenticated operator context](150-operator-access-tokens.md) and a
   protected new token destination. A fully applied baseline and mandatory exact
   ExpectedGeneration remain required. Component/Binding changes alone have zero
   new Resource operations, attempts, generated credentials or privilege effects.
   Commit advances Desired only; explicit Execute advances Applied after validating
   original evidence. Materialization and every new grant remain explicit.

Planning captures the **complete target**: normalized declaration, relevant scope
absence/tokens/decisions, selected exact Profiles, effective documents and Binding
requests, render/formatter pins, effective options and provenance. It does not copy
the old configuration layout. Check and atomic Commit use target Component scopes,
including keys with no assignment row. Relevant insertion (even empty) or a changed
assignment stales the Plan; removed Component scope writes do not stale or block
it. Replan explicitly when intent remains current. Later assignment changes cannot
rewrite a frozen accepted generation or block its Execute/materialization.

## Exact inventory, continuity and retirement

`--all` enumerates only the target's frozen Component/document pairs. Removed
Components/documents and removed Binding projections do not appear there. Current
selection of a removed target returns NotFound; exact existing historical artifacts
and their grants retain original content, scope and audit. A worker-v1 token cannot
retrieve scheduler-v2 or worker-v3. Explicit expiry/revocation remains available.
A valid infrastructure-only target with zero Components succeeds with an empty
zero-document batch; it does not delete historical artifacts or grants.

The original database/role/OIDs, data, management identities, credential SecretRef
and confirmed operation/output history remain authoritative. Reuse also works after
a mixed generation: an independent database created in generation 2 retains that
direct original source alongside the pair's generation-1 sources. The existing
[#160 addition boundary](158-resource-reuse-generation-updates.md#mixed-reuse-and-create-160)
is preserved and is not expanded by declaring a new Binding target.

Removing declarations does **not** stop an old process, revoke its grants, delete
Kubernetes objects, delete assignment history, drop Resources or clean workloads.
[P3 explicit rollout/retirement cleanup](../../ROADMAP.md#p3---workload-lifecycle)
remains open and must account for still-running recipients, owned obsolete objects,
partial failure/retry and explicit grant retirement. This issue implements none of
that cleanup, automatic materialization, access issuance or revocation.

This slice did not admit Resource removal/reintroduction; the subsequent [bounded standalone retained lifecycle](164-retained-resource-reintroduction.md) does. Broader removal/rename/reintroduction, new login/pair/access shapes, Provider
rebinding, rotation, configuration-only updates of the same ManifestRevision,
exports, unsupported templates and Workloads remain outside this slice.
No schema, codec or wire-format change is needed; admitted #158 catalogs need no
reset or backfill. Older catalogs lacking management records retain the existing
update refusal. Startup never resets data or infers external ownership.

## Automated evidence

`GenerationResourceReuseTests` covers ordinary and changed membership through
actual authenticated Host/generated clients, required published Native AOT CLI,
explicit zero-operation Execute, target inventory, received typed timeouts and
real DDL/DML, separate old worker/Binding grants, cross-generation denial, original
history/OIDs, Commit/Execute repeats, no-op, restart before Execute and actual
pg_dump/pg_restore. `GenerationComponentBindingTests` covers relevant absent-scope
insertion/change, irrelevant removed-scope insertion/change, same-ID logical
assignment versus rename, valid zero-Component targets and invalid references,
slots, renderers, collisions and unsupported targets without desired acceptance
or effects. Existing frozen-planning lock/race tests retain atomic capture/CAS gates.

`MixedResourceGenerationTests` changes layouts after mixed creation while retaining
source=2 evidence, old artifacts/grants and real restore. The synthetic typed queue
family renders changed Components/Bindings through the common planner and document
store without shared PostgreSQL contract/field interpretation. Independent contexts
verify persisted state; external OID/ACL/data and known received settings provide
oracles beyond comparing saved values to themselves.

Run all CONTRIBUTING solution gates, the unfiltered measured disposable PostgreSQL
suite, timing artifact safety, dependency review and Windows/Linux Native AOT gates.
Exact executed counts, warnings, skips, source SHA and CI belong in the PR evidence;
these test descriptions alone are not a manual production rehearsal.
