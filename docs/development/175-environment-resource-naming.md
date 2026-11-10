# Environment Resource naming operator path

Read the [contract and catalog boundary](../architecture/resource-naming-v1alpha1.md)
and [authenticated operator setup](150-operator-access-tokens.md) first. Use a
fresh catalog for this alpha model if an older catalog contains planning history.
Resetting a catalog does not remove its external databases or roles.

Prepare Environment `staging`, an administrative secret, a Provider revision and
a successfully preflighted `postgresql.primary` alias using the existing bootstrap
commands. Naming policy administration cannot create or switch that Provider.

Save this non-secret declaration as `naming.json`:

```json
{"mode":"prefixed","prefix":"staging_"}
```

```text
habyctl resource-naming create contour --revision 1 --file naming.json --environment staging
habyctl resource-naming get contour --revision 1 --environment staging
habyctl resource-naming selection postgresql.primary --environment staging
habyctl resource-naming select postgresql.primary --policy contour --revision 1 --if-absent --environment staging
```

Read output includes exact revision, mode/prefix, selection token and audit. After
the first selection, use `--if-match <token>` for an explicit condition, or omit
both conditions for one read-then-conditional selection. Never automatically retry
a conflict. Creating revision 2 alone does not select it. Repeating the same exact
selection with its current token returns Unchanged without changing audit/token.
To select literal names explicitly, import `{"mode":"exact"}` as another exact
revision. No-policy bootstrap also freezes canonical exact mode.

Import the ordinary portable Application declaration containing
`properties.name = orders`. Continue the existing manifest import -> changeset
create -> plan create/get/check -> changeset commit -> explicit generation execute
path. `plan get --output json` includes `targetName` for saved database/login
creation operations; it previews intended names, not observed external objects.
The older request-supplied Provider preview is an exact-mode hypothetical only;
it does not resolve persisted Environment naming selections. Use durable `plan get`
to review the effective names selected for a real deployment.
Materialize exact documents, issue exact delivery grants and use the separately
authenticated receiving client. The delivered database/login names come from
confirmed outputs; renderers/formatters never prefix them again.

For two contours, bootstrap two independent catalogs and Hosts against one
disposable external PostgreSQL 17 server. Use private authenticated CLI contexts
for each Host. Select `staging_` in the first and `staging_shard_` in the second.
Import the exact same ApplicationId/ApplicationVersion/declaration bytes in both.
Their physical databases are `staging_orders` and `staging_shard_orders`; generated
roles also contain their respective prefixes and independent stable owner hashes.
Do not make Environment-specific manifests. Hardened database-owner privileges,
not naming itself, deny cross-database access.

After accepting a generation, changing selection leaves its saved work and old
documents/grants unchanged. A fresh Plan that would move an existing or retained
Resource refuses UnsupportedShape. A Plan saved before a relevant selection change
returns PlanStale at Check/Commit. Restore a compatible original selection before
planning an unchanged update or retained return. Selection identity participates
in significant-input/no-op comparison even if names are equal. No existing
Resource is relocated or credential regenerated to accommodate a policy change.

## Evidence inventory

- `ResourceNamingTests`: strict declarations, canonical-default and exact-selected
  snapshot round trips, literal/no-deduplication behavior, owner separation and
  63-byte database/role boundaries. Focused red/green evidence includes ignored
  prefix behavior and missing canonical null-reference serialization.
- `EnvironmentResourceNamingTests`: identical declaration bytes in two independent
  authenticated production Hosts/catalogs on one disposable server; published
  Native AOT policy create/read/select, import, Plan/Check/Commit, Execute,
  materialization and exact access issuance; received settings, independent
  credentials, real schema/DML, sentinel-before-write preservation, old/new exact
  grants, cross-database denial, pending restart with changed current selection,
  refused relocation and compatible same-revision acceptance.
- `ResourceNamingAdministrationTests`: selection CAS race, exact-mode execution,
  unchanged audit/token, stale equal-content request, missing revision, wrong
  Provider expectation, preparatory/unrelated edits, default-to-explicit Plan
  freshness and cancellation with actual PostgreSQL blocking-pid observation.
- `ResourceNamingConflictTests`: overlapping prefixes in independent catalogs,
  sequential and concurrent external database conflicts, original role/data/access
  preservation and no losing-catalog credential creation/adoption.
- The existing independent-pair generation test additionally runs with both
  prefixes: configuration-only update, complete retained pair removal/return,
  direct original sources, OIDs/ACL/slots/reservations/history, old exact grants
  and actual current-build dump/restore.
- The typed synthetic queue path traverses the actual neutral policy store,
  selection, Plan/Check/Commit, persisted snapshots, reload and resolution dispatch
  with a slash-containing prefix that PostgreSQL rejects.

Run all solution and measured unfiltered PostgreSQL gates, published Windows/Linux
Native AOT gates, dependency/security checks, formatting and diff checks. The
PostgreSQL suite uses disposable Testcontainers or the documented Windows WSLc
helper. Raw private test artifacts and credentials must not be published; publish
only the existing safe timing/artifact allowlist. Test inventory is not a substitute
for reporting actual final-HEAD run results in the implementation handoff.

The production scope remains PostgreSQL database/generated-login naming. RabbitMQ,
Liquid/defaultExpression/parameter execution, relocation, rotation, automatic
cleanup, new recovery capabilities, distributed coordination and P3 Workloads
remain outside this slice.
