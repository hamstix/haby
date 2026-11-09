# Add one independent database/application-login pair

Issue #168 extends the [ADR-045](../adr/0045-application-generation-updates-and-resource-continuity.md)
P2 update boundary. Start with one fully applied pair A, then add one new database
and one new application-login B on the same exact Provider instance, configuration
revision and implementation. The existing `database-owner/v1` policy is unchanged.
All previously managed Resources must be reused unchanged or follow the already
supported standalone retention/reintroduction rules. Initial installation remains
one standalone database or one pair; this is an update path.

## Alpha contract and catalog boundary

Renderer definitions now identify the Resource contract, producer action and output
contract/key; they no longer fix a concrete operation ID for an entire Application.
Planning resolves exactly one producer for the Binding's Resource and action from
adapter-composed nodes. Missing/ambiguous selection refuses with
`render.producer.unavailable`; text and generated JSON retain that safe code. Each immutable render
pin still stores the concrete Resource, operation, action, output contract/key,
formatter, implementations, defaults and consumer options. Both pairs use the same
registered renderer and formatter. Rebuild extensions against this current API.

Pair operation IDs now derive from the declared Resource local ID: database
`<database>-create`/`<database>-restrict` and login `<login>-material`,
`<login>-create`, `<login>-access`. The original example's `db`/`app-login` IDs
remain identical. Custom older pair declarations used global IDs and their evidence
is not translated/backfilled. **Use a fresh disposable catalog for this alpha
rehearsal.** There is no old-alpha compatibility guarantee, migration rewrite,
automatic reset or inference of external absence/ownership. A catalog reset never
removes databases/roles and never authorizes adopting them. No schema migration or
Provider-specific catalog field is added. Current-build retry, no-op, restart and
backup/restore preserve immutable source evidence, documents and grants.

## Operator sequence

Use [authenticated operator setup](150-operator-access-tokens.md), the supported
isolated PostgreSQL contour and logging controls from [runbook 120](120-five-step-operator-path.md).
Install/apply pair A first and write identifiable application data using its exact
delivered document. Retain that document and its scoped grant. Import a new version
keeping A's exact keys, properties and Provider alias. Add, for example:

```json
{
  "db-b": {
    "type": "postgresql.database/v1alpha1",
    "providerRef": "postgresql.primary",
    "properties": { "name": "sample_independent_b" }
  },
  "login-b": {
    "type": "postgresql.application-login/v1alpha1",
    "providerRef": "postgresql.primary",
    "properties": {
      "databaseRef": "db-b", "schema": "app", "privilegePolicy": "database-owner/v1"
    }
  }
}
```

Add a Component with a local Binding to `login-b`, using the same Profile slot and
`postgresql.npgsql-connection/v1alpha1` renderer used by A. api/worker can retain A.
Consumer options may differ; they cannot override database/user/endpoint/password.

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
habyctl configuration access create <application-id> --component consumer-b --document settings --generation 2 --token-output-file <new-private-token-file>
```

Review Reuse A and exactly B's five nodes. Dependencies/selectors refer only to B's
own create/restrict/material/login/access chain. Commit accepts pending state and
new management records without external effects. The login's stable Resource owner
selects its own generated slot, SecretRef and owner-derived reserved role name.
Execution validates all old reuse before dispatch and advances Applied only after
all five new effects and all reuse requirements complete. It creates no fake A
attempts/outputs and never regenerates A's password or replays its privileges.

Inspect a failure before explicitly resuming. Confirmed B nodes remain complete;
uncertain external effects stop without replay/compensation. Existing standalone
manual database assignment does not recover a mixed pair graph. Role/access
recovery and adoption remain unavailable. A retry is not a rollback claim.

Use scoped delivery and the receiving application's typed runtime settings to verify
both pairs, then perform DDL/DML as each login. Verify A's original OIDs, ACL/data,
credential and history independently. Neither application login may connect to the
other database under the accepted PUBLIC restriction. A grant for A's exact artifact
cannot retrieve B's artifact. Ordinary CLI/preview/log output contains no resolved
connection string, raw delivery JSON, password or token.

For v3 retain both pairs and import a new version with ExpectedGeneration 2. Plan
has zero Resource operations and four direct original sources: A database/login=1,
B database/login=2. Execute and materialize explicitly. A later same-revision
[configuration-only update](166-configuration-only-generations.md) uses the same
continuity rules. Old exact grants retain their original configuration throughout;
new documents require explicit new grants. Delivery does not imply process reload.

Previously supported independent standalone additions, retention and compatible
same-key reintroduction can coexist with both pairs. They do not acquire login or
owner policy through reuse. Names remain owned while retained.

## Evidence and limits

Focused tests record actual failing baseline admission and fixed-producer assertions
before production guard changes. Planning tests cover exact IDs, dependencies,
selectors, foreign/extra/missing nodes and unsupported shapes. The persisted Host
rehearsal exercises A -> A+B -> direct reuse -> same-revision configuration change,
independent runtime settings and SQL writes, cross-pair/grant denial, old grants,
slots/management/history, restart before Execute and real pg_dump/pg_restore.
The typed synthetic queue test materializes two distinct expected outputs through
the same renderer contract and direct mixed-generation provenance.

Stage interruption/cancellation tests retain A and completed B work, including the
safe local-material retry and non-replay of uncertain external effects. Corrupt
original evidence refuses new work. Existing lock-observed competing Commit and
acceptance-cancellation tests also exercise pair addition. Complete solution,
measured unfiltered PostgreSQL, timing/export safety, dependency review and published
Windows/Linux Native AOT gates remain required. Exact HEAD, executed counts,
warnings/skips and unperformed checks belong in PR evidence; this document alone
is not a claim of production readiness.

Only one new independent pair per update is admitted. New login on a managed or
retained database, standalone-to-pair promotion, multiple owner logins, changed old
inputs/access policies, Provider rebinding/rotation, pair deletion, general new
dependencies on reused outputs, name release/adoption, automatic materialization/
grants, background reconciliation and Workloads remain unsupported. Full P2
completion and P3 rollout/retirement are separate work.
