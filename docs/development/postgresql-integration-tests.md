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
   connection to the same integration test project used in CI. The test must
   reject a supplied connection string without the explicit opt-in marker.
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

The external mode requires both `HABY_POSTGRESQL_EPHEMERAL_EXTERNAL=1` and
`HABY_POSTGRESQL_EPHEMERAL_CONNECTION`. Supplying only a connection string fails.
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
