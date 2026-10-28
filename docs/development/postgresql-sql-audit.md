# PostgreSQL SQL and trigger audit

- Status: Review draft
- Baseline: `main` at `f0d798144ee9164709d2b64a019feb83f76cb7b7`
- Policy: [ADR-026](../adr/0026-postgresql-persistence-and-server-side-sql-policy.md)

## Purpose and boundary

This audit classifies Haby-owned runtime SQL and the server-side functions
installed by EF Core migrations. It does not change schema behavior. In
particular, the existing integration tests intentionally exercise direct
database writes against several immutability and ordering guards. Removing
those guards while the runtime and migration code use the same database
principal would weaken the accepted persisted-history contract.

Test-only SQL used to inject faults or inspect disposable databases is not
production workflow and is outside the refactoring inventory.

## Runtime SQL

| Site | Purpose | ADR-026 classification | Action |
| --- | --- | --- | --- |
| `PostgreSqlDatabaseGateway` | Inspect `pg_database`; issue quoted `CREATE DATABASE` | PostgreSQL catalog and DDL capability | Retain; keep the values parameterized and identifiers quoted |
| `PostgreSqlProviderRevisionPreflight` | Check PostgreSQL connection and server privileges | Provider-specific capability probe | Retain |
| `PostgreSqlManifestImportStore` | Lock the Application row during immutable import | Explicit row-lock mode | Retain until an equivalent concurrency mechanism is proved |
| `PostgreSqlPlaintextSecretStore` | Lock a Secret before adding a version | Explicit row-lock mode | Retain until an equivalent concurrency mechanism is proved |
| `PostgreSqlDurableDeploymentPlanner` and `PostgreSqlDurableDesiredStateCommitter` | Order ChangeSet, Plan and alias operations | Explicit row-lock modes | Retain; preserve stale-intent versus stale-plan behavior |
| `PostgreSqlDatabaseRecoveryJournal` | Lock the exact uncertain operation | Explicit row-lock mode | Retain; preserve concurrent assignment semantics |
| `PostgreSqlProviderAliasStore` | Bound lock and statement waits | PostgreSQL transaction-local settings | Retain |

The normal data reads and writes around these sites are already expressed
through EF Core. Replacing the listed statements with ordinary LINQ queries
would silently remove their lock, DDL, or catalog semantics; it would not
move business logic out of PostgreSQL.

## Server-side functions and triggers

| Migration / group | Protected invariant | Classification | Safe to remove now? |
| --- | --- | --- | --- |
| `AddPlaintextSecrets`: `reject_secret_version_mutation` | SecretVersion and payload history cannot be rewritten or deleted | Append-only integrity guard | No, not with the current write-capable runtime principal |
| `AddProviderConfigurationRevisions`: `reject_provider_mutation`, `protect_provider_revision_update` | Provider identity, references and sealed revision payloads are immutable | Integrity guards with one controlled seal transition | No |
| `AddProviderConfigurationRevisions`: `check_provider_secret_ref_insert`, `require_provider_revision_seal`, `check_provider_revision_insert` | References cannot be appended after seal; committed revisions are sealed; revision numbers rise under concurrent writers | Cross-table workflow and ordering; strongest refactor candidates | Not without replacing direct-write protection and ordering |
| `AddDurableDeploymentPlanning` and `AddDurableDesiredGeneration`: `reject_deployment_planning_mutation` | ChangeSets, Plans, generations and commits remain immutable | Append-only integrity guard | No |
| `AddPendingCreateOperation` / `AddDurableCreateDispatchBarrier`: durable operation and attempt guards | Operation identity, terminal states and closed attempts cannot be rewritten | State-machine integrity guards | No; a rewrite must preserve uncertain-effect recovery |
| `AddPostgreSqlDatabaseRecoveryDecision`: recovery-decision guard and exact terminal-operation exception | Recovery audit is append-only and only the audited transition may leave `NeedsAttention` | Narrow exception explicitly approved under ADR-026 | Retain pending a replacement threat model |

The Provider configuration store already checks target identity, revision
ordering, SecretRefs, and payload consistency in C#. Its trigger functions
add a separate guarantee for direct database writers and serialize concurrent
revision inserts. Removing only the functions would not complete a migration:
the application would still need a proved concurrency mechanism, and direct
writes could create unsealed or out-of-order history or append references to
an accepted revision.

## Selected direction

There is no low-risk trigger deletion in this inventory that both reduces
server-side business logic and preserves all current direct-write guarantees.
Keep the current single-principal startup-migration model from ADR-023. Do not
add separate migration and runtime database roles for the pilot: the added
installation and administration complexity is not justified by the expected
reduction in server-side guards. Keep the smallest audited trigger exceptions
that protect direct writes, while moving supported workflow decisions to
C#/EF Core. The Provider revision path needs an explicit application-side
concurrency mechanism before its ordering trigger ceases to be relied on.

This choice does not allow editing an already merged migration. Any removal
requires an additive migration and disposable PostgreSQL tests for fresh
installation, upgrade, concurrent writers, direct writes, rollback, and
retained-history reads. Do not disable or drop guards merely to make a test
pass. Reconsider role separation only if a later operational requirement
justifies it; it is not a prerequisite for this audit's refactoring work.
