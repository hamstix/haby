# PostgreSQL checkpoint integration tests

This guide records the test-runtime contract for the PostgreSQL checkpoint. See
[ADR-022](../adr/0022-postgresql-database-checkpoint-execution.md) for the
Resource and execution semantics.

## Runtime choices

| Host | Container launch | Test connection |
| --- | --- | --- |
| Linux CI | `Testcontainers.PostgreSql` over a Docker-compatible API | Container-generated administrative connection |
| Linux developer machine with Docker-compatible runtime | `Testcontainers.PostgreSql` | Container-generated administrative connection |
| Windows 11 with Docker-compatible runtime | `Testcontainers.PostgreSql` | Container-generated administrative connection |
| Windows 11 with built-in `wslc` only | Repository PowerShell helper launches one disposable `wslc` PostgreSQL container | Explicit ephemeral external-connection mode, using the same integration assertions |

`wslc` is a WSL container CLI, not a Docker CLI or a verified Docker API
endpoint. Do not set `DOCKER_HOST` to a guessed `wslc` socket or assume that
Testcontainers can drive it. Testcontainers' documented configuration expects
a Docker-compatible endpoint or context. The WSLc path starts its container
separately and supplies only its temporary connection to the tests.

Official references: [WSL containers](https://learn.microsoft.com/en-us/windows/wsl/wsl-container),
[WSLc getting started](https://learn.microsoft.com/en-us/windows/wsl/tutorials/wsl-containers),
[Testcontainers requirements](https://dotnet.testcontainers.org/) and
[Testcontainers custom Docker configuration](https://dotnet.testcontainers.org/custom_configuration/).

## Windows 11 with WSLc

From the same PowerShell environment that will run `dotnet test`, check:

```powershell
Get-Command wslc
wslc version
wslc run --rm hello-world
```

Microsoft states a minimum WSL version of 2.9.3. Version output alone does not
prove that a local agent's terminal can access `wslc` or start a container.
A missing command,
access-denied result or failed `hello-world` run is an environment blocker,
not a failed PostgreSQL provider test. The agent should report it; it must not
install a new runtime or switch to a personal PostgreSQL instance silently.

Run `scripts/test-postgresql-wslc.ps1` from the repository root. It:

1. Verify `wslc` and the .NET SDK in its own process environment.
2. Choose a unique test-owned container name, an available loopback host port
   and a fresh temporary administrative password. Never use a fixed container
   name, host port 5432 or a stored credential.
3. Start one PostgreSQL image pinned by the test suite, with its port published
   only on `127.0.0.1`, and wait until Npgsql can connect. The command shape is
   `wslc run -d --rm --name <test-owned-name> -p 127.0.0.1:<port>:5432 -e POSTGRES_PASSWORD=<temporary-value> <pinned-postgres-image>`;
   verify exact option support with `wslc run --help` on the host before use.
4. Enable a distinct opt-in external-ephemeral mode and pass the temporary
   connection and test-owned container name to the same integration test
   project used in CI. The test must reject a supplied connection string
   without the explicit opt-in marker. The container name lets the backup
   drill run `pg_dump` and `pg_restore` inside that disposable container.
5. In `finally`, stop **only** the container this invocation created, clear
   temporary environment variables, and report cleanup failure separately.
   Do not run broad prune commands or touch other containers.

The helper must not print the temporary password or connection string. It may
collect container logs on failure only after checking that they do not expose
secrets. A failed test does not authorize leaving the test container running
indefinitely. An explicitly requested diagnostic mode may preserve it with
the exact container name reported to the operator.

The helper exercises the same real-PostgreSQL assertions as CI. Direct `dotnet test` from Windows without
a Docker-compatible endpoint or the helper is not evidence of a provider bug.

## Linux and Docker-compatible runtimes

Verify the runtime from the shell that will run tests:

```sh
docker info
dotnet --info
```

The separate `tests/Haby.PostgreSql.IntegrationTests` project uses `Testcontainers.PostgreSql` by
default. It starts and disposes its own container; no developer database,
hard-coded host port, checked-in password or manually created role is needed.
CI runs this project in a dedicated required job and fails visibly when its
Docker-compatible runtime is unavailable. Ordinary solution unit tests remain
independent of containers; they do not silently skip an integration assertion.

Run the integration project directly on a Docker-compatible host:

```sh
dotnet test ./tests/Haby.PostgreSql.IntegrationTests/Haby.PostgreSql.IntegrationTests.csproj --artifacts-path ./.artifacts
```

The external mode requires `HABY_POSTGRESQL_EPHEMERAL_EXTERNAL=1`,
`HABY_POSTGRESQL_EPHEMERAL_CONNECTION` and
`HABY_POSTGRESQL_EPHEMERAL_CONTAINER`. Supplying only a connection string fails.
Use this mode only with the helper's disposable PostgreSQL container.

## Dependency review for Slice 12

The provider uses [Npgsql 10.0.3](https://www.nuget.org/packages/Npgsql/10.0.3)
under the PostgreSQL license. Its .NET 10 dependency is
`Microsoft.Extensions.Logging.Abstractions` (resolved as 10.0.0, MIT). The
separate test project uses [Testcontainers.PostgreSql 4.16.0](https://www.nuget.org/packages/Testcontainers.PostgreSql/)
under MIT; it depends on Testcontainers 4.16.0 (MIT). The resolved container
dependency tree includes Docker.DotNet.Enhanced and its Handler.Abstractions,
LegacyHttp, NativeHttp, NPipe, Unix and X509 packages (all 4.3.3, MIT),
SSH.NET 2026.0.0 (MIT), BouncyCastle.Cryptography 2.7.0 (MIT), and
SharpZipLib 1.4.2 (MIT). NuGet's resolved-package advisory check reported no
vulnerable package; the required PR dependency-review job checks licenses and
advisories again in CI. No WSLc library package is used.

## Test ownership and safety

- The integration fixture creates only uniquely named test databases in its
  disposable PostgreSQL instance and verifies existence through an independent
  connection or catalog query.
- The integration suite covers confirmed retry, a pre-existing database
  conflict, invalid names and visible failures with real Npgsql. The fast
  Application tests cover ambiguous post-dispatch outcomes through Haby's
  execution boundary without treating them as success.
- It never targets a maintainer's persistent PostgreSQL by default. A separate
  manual smoke-test mode requires an explicit opt-in and an isolated target.
- Npgsql is real in the provider integration test. Fast orchestration tests may
  fake only Haby's own execution interface.

## gRPC checkpoint (Slice 13)

The integration-test assembly contains a private `.proto`, generated client and
server, and an ephemeral Kestrel host bound to an OS-assigned loopback HTTP/2
port. The client sends the original UTF-8 envelope and calls Import,
CreateChangeSet, Plan, CommitDesiredState and ExecuteCommittedGeneration
separately. The test queries `pg_database` through an independent Npgsql
connection before and after execution. The existing CI `postgresql-integration`
job and Windows WSLc helper run this same test project. No production server or
public gRPC contract is introduced.

The test host owns the administrative connection and exact in-memory Provider
configuration. Import, planning and desired-state commit execute no SQL.
Execution alone calls the PostgreSQL provisioner. The test verifies equivalent
import identity, commit and execution retries, invalid input without a revision
or database, and an existing-database conflict that leaves AppliedGeneration
unset. State disappears with the host.

Dependency review (new packages only):

| Package | Version | Purpose | License |
| --- | --- | --- | --- |
| `Grpc.AspNetCore.Server` | 2.84.0 | Private test RPC host | Apache-2.0 |
| `Grpc.Net.Client` | 2.84.0 | Real generated test client transport | Apache-2.0 |
| `Grpc.Tools` | 2.84.0 | Build-time protobuf and gRPC generation, private assets | Apache-2.0 |
| `Google.Protobuf` | 3.36.2 | Generated message serialization | BSD-3-Clause |

The resolved new runtime dependencies are `Grpc.Core.Api` and `Grpc.Net.Common`
2.84.0, both Apache-2.0. The ASP.NET Core shared framework supplies Kestrel;
no TestHost or extra container package is needed. CI dependency review remains
the advisory and license gate for the final package graph.

This test validates the accepted boundaries for one database Resource, not
durable operations, PostgreSQL-backed Haby state, authorization, stable public
APIs, generated credentials, SecretVersions, configuration publication,
deprovisioning or Kubernetes integration.

## Immutable manifest catalog (Slice 14)

The same separate integration project and disposable PostgreSQL runtime now
exercise the EF Core manifest catalog. Each catalog test creates a distinct
database inside the test-owned container. Tests apply the initial migration,
import through `ImportApplicationManifest`, dispose the first service provider,
then read through a fresh provider against the same database. They also check
database uniqueness, rollback on a failed revision insert, concurrent imports,
stored snapshot and warning failures, and startup migration failure. The
solution's `Haby.Persistence.PostgreSql.Tests` project checks snapshot codec
round-trip and corrupt input without PostgreSQL. The in-memory checkpoint host
remains a separate test boundary.

The runtime packages are `Microsoft.EntityFrameworkCore` 10.0.13 (MIT),
`Microsoft.EntityFrameworkCore.Relational` 10.0.13 (MIT), and
`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 (PostgreSQL license) for catalog
persistence. `Microsoft.EntityFrameworkCore.Design` 10.0.13 (MIT) is a private
design-time dependency for migrations. The initial migration is checked in.
The existing Npgsql and Testcontainers packages are reused; no new container
or network transport dependency is introduced. See the catalog project's README
for startup migration, backup and snapshot compatibility requirements.

The shadow-preview integration test imports a revision, restarts the alpha host,
requests preview through the generated client, and independently checks that
the requested target database is absent. It also checks repeated preview,
typed failures, historical warnings and the absence of deployment or operation
tables in the catalog. It uses the same disposable PostgreSQL runtime.

The restore integration test stops its source host, runs the PostgreSQL image's
`pg_dump` and `pg_restore` utilities, restores into a separate empty database,
compares Application and revision counts plus migration IDs, then reads the
same immutable revision, warnings and hypothetical preview through a fresh
host. The archive exists only inside the disposable container and is removed
after the test. The operator procedure is in
[the shadow-pilot rehearsal](shadow-pilot-rehearsal.md).

The [active-pilot restore rehearsal](active-pilot-restore-rehearsal.md) extends
this real dump/restore proof to Pending, Dispatching, NeedsAttention, confirmed
success and manually assigned success. It compares complete read responses and
private content digests through independent contexts, resolves pinned secrets
internally, and verifies protected audit preservation. The fresh restored Host
must perform zero Provider reads/dispatches, including on assignment retry.
Its private container-only archive and utility-error redaction prevent protected
data from reaching artifacts; cleanup preserves an original test failure.

## Environment administration (issue #87)

The same disposable PostgreSQL suite exercises explicit create/get/list through
production Host composition and the generated alpha gRPC client. A synchronized
16-request race must produce exactly one Created outcome and one persisted
UUIDv7 identity; retries through the compatibility SecretStore helper and a
fresh Host must return that identity. Independent DbContexts verify persisted
metadata and zero rows in secret, Provider, deployment, planning and operation
tables. A direct duplicate insert still proves the database unique constraint.
Ordinal pagination is checked with more than 100 distinct names, maximum-length
names, sizes 1/2/50/100, exact boundaries, unknown continuation positions and
typed invalid cursor/page-size outcomes. Arrange, act and assert contexts are
separate; verification queries do not rely on tracked entities.

When `HABY_AOT_CLI_REQUIRED=1`, the suite also launches the published CLI against
the restarted Host for create retry, get/list, local context reads, text/JSON
output and expected rejections. Selecting a context with an absent Environment
must leave it absent. The existing Native AOT matrix also covers the new command
tree and continuation output through disposable loopback generated services.

## PostgreSQL execution boundary (issue #90)

`Haby.Application` owns durable operation keys/states, journal/query contracts,
the public executor and safe execution results. `Haby.PostgreSql` owns
`PostgreSqlDatabaseCreateInput`, `IPostgreSqlDatabaseCreateTarget`,
`PostgreSqlDatabaseAbsenceOutcome` and `PostgreSqlDatabaseCreateDispatchOutcome`.
The input retains the exact Environment, Provider instance, configuration revision,
implementation and database name. The adapter resolves the exact configuration's
SecretRef; execution and recovery never follow the current Provider alias.
The existing singleton executor commits the durable dispatch marker between the
absence read and SQL dispatch. ADR-025 recovery remains explicitly specialized.
No persistence schema or wire contract changes are needed for this source move.

Within this adapter, an existing database or a definitive duplicate response maps
to Conflict; a definitive PostgreSQL rejection maps to ServerRejected. Those
shared reasons remain general conflict/rejection categories. The adapter-local
DatabaseNameInvalid outcome maps to the unchanged durable DatabaseNameInvalid
reason. This shared specialized reason is temporary compatibility debt under
[ADR-031](../adr/0031-postgresql-execution-boundaries-and-reason-compatibility.md):
issue #91 must define a Provider-neutral successor and explicit legacy mapping,
implemented before role/grant or any other new effect kind is admitted.

The boundary tests check adapter ownership and the absence of an Application
reference to the concrete adapter. The disposable PostgreSQL suite checks the
literal persisted `DatabaseNameInvalid`, fresh-context history and recovery reads,
and terminal retry without dispatch. CLI tests distinguish protobuf value `4`
and JSON `databaseNameInvalid` in historical operation/recovery presentation;
the published Native AOT loopback test also renders the historical operation.
The existing dispatch, cancellation, uncertainty, pinned-input, recovery, restart
and dump/restore suites continue to exercise the same execution behavior.
