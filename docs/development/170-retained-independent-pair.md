# Retain and reintroduce an independent database/login pair

Issue #170 extends [ADR-045](../adr/0045-application-generation-updates-and-resource-continuity.md)
to the complete independent `database-owner/v1` pair added by
[#168](168-independent-database-login-pair.md). Install/apply pair A at generation 1,
add/apply pair B at generation 2, then remove both B declarations while A remains
declared unchanged. Retention preserves B's database, login, data, ownership, ACL,
generated password and names. Old clients can still connect.

## Remove both declarations and their consumers

Import a new ApplicationVersion removing `db-b` and `login-b` together. Both original
Resources must have default/explicit `retain` deletion policy. Remove B's Component
or its Bindings and adjust the selected Profile/document layout together under
[the #162 rules](162-component-binding-generation-updates.md). No target Binding or
Resource reference may point to either absent declaration; required slots and
projection validation still apply. Keep A's original keys, definitions, relationship
and exact Provider instance/configuration revision/implementation.

Use the [authenticated operator context](150-operator-access-tokens.md):

```text
habyctl manifest import application-v3.json
habyctl changeset create <application-record-id> <v3-revision-number> --expected-generation 2
habyctl plan create <changeset-id>
habyctl plan get <plan-id>
habyctl plan check <plan-id>
habyctl changeset commit <changeset-id> --plan <plan-id>
habyctl deployment generation get <deployment-id> 3
habyctl deployment generation execute <deployment-id> 3
habyctl configuration materialize <application-id> --all --generation 3
```

Review two A Reuse decisions and two B Retain decisions, with B's unchanged
management IDs and direct source generation 2. There are zero external operations.
Commit accepts the generation and both immutable memberships in one transaction;
Applied remains 2 until explicit Execute validates original evidence and advances
it to 3. There is no partial peer acceptance, copied attempt/output, password
generation, CREATE, privilege replay, owner change, deletion or reservation release.
Plan/generation text and generated JSON expose these existing typed inventories.

Execute checks both exact registered retention policies, original creation/actions,
typed outputs, owner/purpose/material metadata and reserved role name. The adapter
validates the original dependency closure against its own frozen declaration,
including when the target omits B. Missing/corrupt/foreign evidence refuses without
repair or Applied advancement. No external drift probe is performed. Restart after
Commit and explicitly Execute the saved generation; do not create replacement intent
or reset a catalog to recover a missing response/support.

## Absence, exact access and compatible return

Further absent generations retain both B identities and their direct source=2.
A supported [same-revision configuration update](166-configuration-only-generations.md)
can accept new Profile inputs without bringing B back. `--all` enumerates only the
target documents; it does not enumerate B's removed consumer or retire old artifacts.

Old B grants remain authorized for their exact original artifacts until explicit
expiry/revocation. They return the old configuration and original password and can
still access retained B. A grant never follows the logical Component into a newer
generation. Declaration removal is not an access revocation boundary.

To return B, import a new version restoring BOTH exact original stable keys and
definitions, including `databaseRef`, schema, privilege policy, lifecycle and Provider
binding. Restore a valid consumer/layout, create a Plan with the current exact
ExpectedGeneration, and review both B Reuse entries with `reintroduced: true` and
direct source 2. Commit and Execute explicitly. Existing evidence and SecretRef are
used without recreating B, generating material, acquiring reservations or replaying
privileges. Materialize new target documents and issue new exact grants explicitly;
old B grants cannot retrieve the returned consumer's new artifact.

Half-pair removal/return, a new key for either peer, retained-name claims, swapped
database selectors, changed inputs/policies, Provider rebinding, standalone-to-pair
promotion and ownership transfer refuse. The bounded removal retains one original
complete pair while another original compatible pair remains active. Last-pair,
all-Resource and whole-Application removal remain unsupported. Earlier standalone
retention/addition, pair addition, reuse and configuration-only paths remain available.

An independent standalone database has its own lifecycle even if it was declared
when B was created. For example, A at generation 1, standalone S at 2 and B at 3
may retain S and B in either order while A remains unchanged. Their direct sources
stay A=1, S=2 and B=3 through further absence and compatible return. B's closure
does not require S to remain declared; S still requires its own exact policy,
management, evidence and name ownership. This does not admit removal of another
original pair or weaken half-pair/name/ownership guards.

## Catalog and evidence boundary

No schema, wire or graph-codec change, migration, backfill or catalog reset is needed.
Existing generic immutable membership and management records represent both peers;
pair policy stays in the PostgreSQL adapter. The #168 alpha input/producer contract
boundary still applies; older catalogs without supported management/evidence are
not upgraded by inference. Startup never resets persistent data.

Automated evidence covers actual authenticated Host/generated clients and required
published Native AOT CLI, distinct original sentinels seeded once at creation,
OID/owner/ACL/history/credential/name continuity, multiple absent generations,
configuration-only absence, exact old/new grants, no-op/replay, restart and real
catalog `pg_dump`/`pg_restore` while absent and after return. Every later runtime
probe reads the original sentinel before writing to a separate runtime table.
Missing data is never repaired/reseeded. Lock-observed competing Commit and
cancellation tests check both-peer membership/receipt atomicity. A dependent typed
synthetic family traverses shared persisted policy dispatch without database policy
in common layers. Full solution, measured PostgreSQL, timing/artifact safety,
dependency and Windows/Linux Native AOT gates remain required; exact executed
counts, warnings/skips, HEAD and CI results belong in the PR evidence.

P3 rollout/retirement and explicit grant/Workload cleanup remain deferred. External
database/role/credential deletion or revocation, rotation, name release/adoption,
automatic cleanup/materialization/grants and Workloads are outside this slice.
