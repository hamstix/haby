# PostgreSQL isolation and resource groups

This is ADR-056 maintenance slice 2, based on main
`1720256bd200bf75c67f74829cb3b45afa46651f`. Use the unchanged
[hosted entrypoint](193-hosted-postgresql-entrypoint.md). No production contract,
migration, package, transport, timeout or pool policy changes are included.

## Resource bound

`HABY_TEST_POSTGRESQL_SERVERS` selects **2 through 8**, default **4**. Only those
single-digit values are accepted; malformed values fail preflight before Docker
or test dispatch. Unset selects 4. For example:

```sh
export HABY_TEST_POSTGRESQL_SERVERS=4
pwsh -File scripts/test-postgresql-measured.ps1 -Docker \
  -ResultsDirectory .artifacts/postgresql-measurement
```

Four is a conservative starting resource budget: one catalog group and up to
three independent heavy fixture groups. It is not derived from CPU count or a
guaranteed speedup. The reference run measured long serial spans in Application
Login (347 rows, 919.14 seconds) and Host (98 rows, 222.72 seconds), with 1195.59
seconds in the full test phase. It did not record peak live containers or memory;
the new bound must be assessed using the current run's counts and timings. Its
setup stream contains 20 readiness events (46.80 seconds total) and 20 cleanup
events (20.01 seconds total); those totals are overlapping observations, not an
additive saving estimate or proof of the previous live-server peak.

The small `IXunitTestCollectionFactory` uses xUnit 2 collection lifecycle:

- One named catalog collection owns one server for five audited classes.
- Other eligible class fixtures are distributed deterministically among
  `limit - 1` lanes. Classes within each lane run sequentially and dispose their
  server before the next class starts. Servers are never shared across these
  class fixtures.
- Existing exclusive Provider alias collections are retained. Only the three
  client/global/server logging rows retain the exclusive Login collection.
- The actual Docker infrastructure controls use an exclusive collection.
  Fast tests retain ordinary collections and require no server.

There is **no waiting semaphore** inside fixture initialization. A fail-closed
reservation check rejects an unexpected extra fixture immediately; it cannot
occupy an xUnit slot while waiting for another fixture to reach disposal.
The framework controls collection execution and internal product concurrency
tests retain their existing barriers, locks and cancellation checks. The runner
does not globally serialize tests or inflate deadlines. Configuring two servers
still permits a catalog group and one owned heavy lane concurrently.

The xUnit extension uses the framework's discovery/collection metadata only in
the test assembly; it adds no production reflection or Native AOT dependency.

## Fixture audit and effective case obligations

The [coverage map](../../tests/Haby.PostgreSql.IntegrationTests/coverage-map.json)
assigns every actual discovered case its effective fixture requirements. These
are conservative unions for mixed classes, not claims that every method performs
every listed effect. The audit below records why a class can or cannot join the
catalog group. Scope follows actual SQL/helpers, not the responsibility label.

| Class/group | Actual effects and retained scope |
| --- | --- |
| `ManifestCatalogPersistenceTests` | Own catalog databases; migration/immutability/conflict assertions; own database dump/restore and unique dump file. No roles, ALTER SYSTEM or global log capture. Shared server, case-owned databases. |
| `ConfigurationProfilePersistenceTests` | Own databases, immutable Profile snapshots, real insert/lock races and database-local restore. No role or server-setting effects. Shared server, case-owned databases. |
| `AdministrativeSecretTests` | Own catalog/secret rows, corruption/migration/ownership checks. No external roles or server settings. Shared server, case-owned databases. |
| `PlaintextSecretStoreTests` | Own secret catalog databases and exact-byte dump/restore. No roles or server settings. Shared server, case-owned databases. |
| `PlacementRevisionPersistenceTests` and partials | Own revision/selection databases, lock races and restore. The one-connection pool oracle owns an `await using NpgsqlDataSource` that closes before case cleanup. Shared server, case-owned databases. |
| `GeneratedCredentialSlotTests` | `Restricted_SQL_writer_can_create_and_read_but_cannot_mutate_generated_history` creates a NOLOGIN role and uses SET ROLE; restore also remains real. Dedicated class server. |
| `ProviderConfigurationStoreTests` | Creates a reader LOGIN role and tests real authentication/permissions. Dedicated class server. |
| `PostgreSqlPairPersistenceTests` | Includes a strict `pg_roles LIKE 'haby_a_%'` absence oracle, tampered graph/snapshot state, session replication-role settings and real restore. Its Persistence label does not permit reuse with catalog-only cases. Dedicated class server. |
| `DurableDeploymentPlanningTests` | Own catalog planning/locking plus provider administrator roles and target database boundaries. Dedicated class server. |
| `DurableExecutionTests` | Real role/database creation, privilege changes, pinned provider targets and recovery. Dedicated class server. |
| `ExactActionAdmissionTests` | Mixed durable catalog/graph/concurrency and synthetic effect boundaries. Retains a dedicated class server; not enrolled as a newly audited catalog-only group. |
| `PostgreSqlIntegrationTests` | Real database Provider effects, global database existence/conflict checks, checkpoint Host and generated RPC client. Dedicated class server. |
| `PostgreSqlRestrictTests` | Actual login role, object ownership/privileges/default settings and refusal oracles. Dedicated class server. |
| `ProviderDeclarationTests` | Catalog declaration/transfer/typed adapter scenarios. Retains its existing stronger dedicated fixture in this bounded migration. |
| `ProviderAliasTests`, `ProviderPreflightCompatibilityTests`, `OperatorProviderAccessTests` | Real administrator/alias/preflight/Host boundaries; existing exclusive Provider alias collection and separate class servers retained. |
| `OperatorHostIntegrationTests` and partials | Real Host/CLI, provider roles/databases, restore, auth/credential and recovery scenarios. Dedicated class server; broad Host migration remains slice 3. |
| Application Login scenario groups | Real roles, credentials, ACL/ownership, original evidence, native/Host/configuration and restore as applicable; separate dedicated class servers, described below. |
| `TestCatalogPoolTests` | Actual bounded pools, session reset/waiters and `pg_stat_activity` absence. Dedicated class server; existing limits/reset/ownership assertions unchanged. |
| `OwnedInfrastructureTests` | Fresh owned servers for capacity, global role sentinel, database routing and lifecycle-fault controls; exclusive, within the same global bound. |
| Five planning/registration classes | `ExactActionRegistrationTests`, `FiveStepCompositionTests`, `IndependentPairPlanningTests`, `PostgreSqlPairPlanningTests`, `RetainedPairPlanningTests`: no server. |
| Host diagnostics/configuration and layout controls | `HostFailureDiagnosticsTests`, `OperatorHostConfigurationTests`, `FixtureLayoutTests`: no PostgreSQL. Actual loopback Host assertions remain unchanged. |

Each catalog case tracks the database name before CREATE dispatch. It creates a
unique database, keeps all existing independent arrange/act/assert DbContexts,
then drops every owned database after the test body and its services/connections
have been disposed. No global pool clearing, forced session termination or shared
catalog database is used. Failed drops remain cleanup failures; xUnit retains
body and cleanup failures separately. Unknown or unrelated databases are never
cleanup candidates. Restore utility files retain their existing unique ownership
and cleanup. The fixture server is removed once its bounded collection finishes.

## Application Login split

The original 347 parameter rows map to these dedicated fixtures:

| Destination | Baseline rows | Responsibility |
| --- | ---: | --- |
| `PostgreSqlApplicationLoginTests` | 80 | Login/access credentials, direct evidence and refusal boundaries |
| `ApplicationConfigurationTests` | 60 | Rendering, materialization and authorized delivery |
| `ApplicationGenerationTests` | 204 | Naming, generation continuity, retention, mixed/independent resources |
| `ApplicationLoginLoggingTests` | 3 | Process-global Npgsql parameter logging, ActivityListener/EventListener and ALTER SYSTEM log capture |

An abstract test-only helper holds the existing scenario construction and
per-case `TestCatalogPools` ownership. It contains no test methods or shared
mutable scenario state. Only the logging group changes Npgsql's global logger;
its exclusive collection preserves setup/reset and all redaction oracles.
There is no removed `DisableParallelization` attribute on the logging collection.

## Case mapping and controls

The [baseline ledger](../../tests/Haby.PostgreSql.IntegrationTests/baseline-cases.json)
contains all 869 hashed case identities from run 379, whose tested tree became
the source main. The [method map](../../tests/Haby.PostgreSql.IntegrationTests/case-migrations.json)
records every original Application Login method's destination, including unchanged
methods. Runtime coverage records both current and original identities, hashing
the qualified method plus LF plus display name in both namespaces. Theory values
remain private. Counter comparison preserves repeated rows; missing/misrouted
baseline cases fail before execution. The separate actual discovery/TRX Counter
comparison still rejects omissions, extra executions and skips after execution.

Four real Docker controls prove distinct system identifiers up to capacity,
fail-fast overflow, a foreign-server role sentinel invisible to a strict isolated
absence oracle, distinct case databases on the eligible shared server, removal
after sessions close, cleanup after an actual started-container fault, and
cleanup after a body failure. Deliberately routing the refusal oracle to the
sentinel server is checked to fail. Deterministic layout/bound checks complement
these controls; count-only mocks do not replace them.

`safe/resources.json` contains only numeric counts: selected bound, active/peak
reserved and ready-but-not-disposed servers, normal-suite versus control peaks, startup/ready/cleanup/fault
counts, overflow refusals, and case-owned catalog create/drop counts. Reservations
include in-flight startup until positively completed disposal, so the peak is a
conservative upper bound on actual live fixture servers. Preflight PostgreSQL and
Testcontainers' Ryuk helper are reported/owned separately, not included in this
fixture-server limit. Deliberate post-start fault injection increments the startup
failure count even though cleanup succeeds. An unresolved cleanup keeps its
reservation and fails the resource report; no endless recovery is attempted.
The runner requires zero active/cleanup failures, matching startup/disposal and
catalog create/drop counts, and peak within the selected bound. Existing safe
timings, source attribution and 14-day retention apply; no credentials, SQL,
database names or container settings are added to public counts.

Compare actual class spans, test phase, setup/cleanup counts and peaks with the
reference without summing overlapping class spans or promising a percentage.
Different hosted SDK patches, machines and schedules prevent a controlled timing
comparison; no statistical reruns are required.

## Legacy and remaining work

The non-Docker entrypoint and WSLc fixture/client path remain. Their external mode
still points at a shared server and cannot prove server isolation; this is not
silently repaired here. The collection factory retains exclusive Login grouping
for external callers. New owned-resource controls explicitly refuse that mode
before creating sentinels. Use the primary Docker gate for complete qualification;
the legacy path remains available for its existing focused diagnostics.

No local full PostgreSQL run is required merely to duplicate hosted evidence.
Windows/Linux published Native AOT, solution/security and full current hosted
PostgreSQL checks remain required. Historical TLS cause remains UNKNOWN under
#188; failures remain blocking. #188/#191 stay stopped. Host/E2E relocation,
qualification triggers and obsolete-path removal remain a separate slice.
