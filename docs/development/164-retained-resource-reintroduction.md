# Retained standalone Resource removal and reintroduction

Issue #164 implements the bounded retained lifecycle under
[ADR-045](../adr/0045-application-generation-updates-and-resource-continuity.md).
Install and fully apply the managed database/login pair at generation 1, then
[add one independent standalone database](158-resource-reuse-generation-updates.md#mixed-reuse-and-create-160)
at generation 2. The pair must stay declared throughout this procedure.

## Remove only the standalone declaration

Import a new ApplicationVersion omitting the standalone Resource, with no dangling
references. Keep the pair's exact keys, contracts, properties, Provider alias,
resolved Provider instance/configuration revision and implementation. The original
standalone declaration must use the default/explicit `retain` deletion policy.
Delete/orphan policies, pair/owner/all-Resource removal and unsupported target
layouts refuse; no destructive operation is substituted.

Use the existing authenticated operator context and exact generation precondition:

```text
habyctl manifest import application-v3.json
habyctl changeset create <application-record-id> <revision-number> --expected-generation 2
habyctl plan create <changeset-id>
habyctl plan get <plan-id>
habyctl plan check <plan-id>
habyctl changeset commit <changeset-id> --plan <plan-id>
habyctl deployment generation get <deployment-id> 3
habyctl deployment generation execute <deployment-id> 3
```

Review two active Reuse entries and one Retain entry with its unchanged management
ID and original source generation 2. This update has zero external operations.
Commit atomically accepts generation 3 and its explicit retained membership;
AppliedGeneration remains 2 until explicit Execute validates the accepted evidence.
Retention does not probe or repair the external database. No DROP, CREATE, fake
attempt/output, password generation, role/ACL change, secret disposal or reservation
release occurs. Data, OID, owner and existing privileges remain as they were.

The immutable graph in every accepted generation records all retained identities,
including through further absent generations. The stable `resource_management`
row and its original source are never replaced or deleted. Membership is a narrow
typed common snapshot: active reuse, retained after removal, and reintroduced
reuse are distinct. No mutable membership write can escape the generation's
acceptance transaction. Exact no-op/replay preserves the existing generation/audit;
it creates no second transition. History/get returns the saved membership.

## Reintroduce the same compatible key

For example, accept and explicitly execute another absent generation 4, then
import v5 restoring the exact standalone Resource local ID and accepted definition.
Use expected generation 4, review Reuse with `reintroduced: true` and original
source 2, then Commit and Execute generation 5 separately. Reintroduction consults
all managed/retained identities rather than treating absence in v4 as permission
to install anew. It uses generation 2 directly, without walking an absent-generation
chain or copying outputs into generation 5.

Changing that key's type, properties/name, lifecycle policy or exact Provider
binding refuses. A different key claiming the retained database name in the target
also refuses; declaration absence never releases its managed name. Existing shared
name reservations remain untouched. Collisions with other external objects retain
ordinary no-adoption behavior. Missing/corrupt/foreign management, original source,
successful attempt or required typed output fails closed; there is no regeneration,
implicit adoption or recovery fallback. The exact registered adapter must still
admit reuse/retention at Execute. Common storage/runner/Host/CLI interpret no
database properties or naming policies.

ExpectedGeneration, fully applied baseline, exact input freshness, uncertainty and
explicit execution boundaries still apply. Competing membership decisions serialize
at Commit through the existing deployment lock. Cancellation before acceptance
rolls back both generation and membership; a confirmed acceptance keeps its exact
receipt. After response loss or restart, inspect/repeat the exact ChangeSet/Plan
and explicitly Execute its committed generation. Do not guess a replacement
generation or reset the catalog. Original creation evidence and retained namespace
survive current-build catalog backup/restore.

## Configuration, catalog and cleanup boundaries

Changed Components/Bindings remain governed by
[runbook 162](162-component-binding-generation-updates.md). `configuration
materialize --all` enumerates only the target's frozen Component/documents.
The retained standalone database requires no new renderer, login or consumer.
Materialization and grant issuance remain explicit. Existing exact documents and
grants keep their original configuration and authorization until explicit revoke
or expiry, regardless of current Resource membership. Declaration removal does not
stop old clients or remove historical assignments, privileges or data ownership.

No schema migration or catalog reset is required for catalogs with the current
management records. Optional typed graph fields store retained membership and
reintroduction; graphs without these decisions keep their previous encoding and
fingerprints. Current-build readers validate the complete typed inventory and
original evidence. Older binaries cannot read newly extended graphs; no downgrade
compatibility is claimed. Catalogs without #158 management records are not
backfilled and still cannot update. Startup never resets persistent data.

Destructive deprovisioning, pair/all-Resource removal, release/transfer of retained
names, rotation, general Resource additions, Provider rebinding, same-revision
configuration-only intent and Workloads remain unsupported. P3 rollout/retirement
and explicit grant/Workload cleanup remain tracked and incomplete; no automatic
cleanup, materialization, access issuance or privilege retirement is introduced.

## Automated acceptance evidence

The PostgreSQL integration suite covers authenticated production Host/generated
clients and required published Native AOT CLI, sentinel data in both databases,
unchanged OIDs/owner/ACL, management/source/attempt/output/credential/reservation
history, v1 -> additive v2 -> absent v3/v4 -> compatible v5, exact old grants,
no-op/replay, restart before Execute and actual pg_dump/pg_restore. Dedicated
tests cover retained-name/incompatible-definition refusals, corrupt/missing/foreign
evidence, missing adapter policy/registration, lock-observed competing Commit and
cancellation around atomic acceptance. A typed synthetic queue exercises the same
persisted retain/reintroduce/original-source path; it is not another production
Provider. Separate arrange/act/assert contexts and external data/catalog oracles
are used. Full gates, timing/artifact safety and dependency checks stay enabled.

Executed counts, exact source SHA, failures, warnings/skips and CI belong in the
PR evidence. This runbook does not itself claim manual production acceptance or
that every gate passed.
