# Hosted PostgreSQL entrypoint

Run from a clean checkout of the PR source on Linux with a local Unix-socket
Docker-compatible engine, PowerShell 7 and the .NET SDK selected by `global.json`:

```sh
bash scripts/ensure-native-aot-prerequisites.sh
export HABY_AOT_C_COMPILER="$(readlink -f "$(command -v clang)")"
dotnet publish src/habyctl/habyctl.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishAot=true -p:TreatWarningsAsErrors=true \
  -p:CppCompilerAndLinker="$HABY_AOT_C_COMPILER" -p:LinkerFlavor=bfd \
  -o .artifacts/aot/linux-x64
export HABY_AOT_CLI_REQUIRED=1
export HABY_AOT_CLI_PATH="$PWD/.artifacts/aot/linux-x64/habyctl"
pwsh -NoProfile -File scripts/test-postgresql-measured.ps1 -Docker \
  -ResultsDirectory .artifacts/postgresql-measurement
```

The prerequisite script validates the compiler resolved from `PATH` and bfd
selection; the command above exports that same compiler. In Actions
the existing prerequisite step supplies it through `GITHUB_ENV`. Use a fresh
results directory on every invocation. The entrypoint does not publish a CLI,
install Docker/SDKs, switch to a developer PostgreSQL, prune resources or retry
the suite. No WSLc session, private supervisor or SSH setup is required.

The existing `postgresql-integration` job calls this command after the unchanged
warnings-as-errors native publication. All other solution, dependency and
Windows/Linux Native AOT jobs remain required. A full local Windows PostgreSQL
run is not required for symmetry; the existing WSLc command/default measured
mode is retained unchanged. This slice does not complete #188/#191.

## Preflight and ownership

Before restore/build/test, the Docker mode checks the supported SDK feature band
and patch, .NET/ASP.NET Core 10 runtimes, source/assets and artifact writability,
required native CLI help/version, native Unix temporary
directory/file modes 0700/0600, at least 1 GiB free at source and temporary paths,
and a reachable Docker API. These are safeguards, not a suite capacity guarantee.
`TMPDIR` must already have working Unix semantics; no remount/permission relaxation
is attempted. External-ephemeral settings are rejected. The selected local
Unix endpoint is assigned to `DOCKER_HOST` only in the runner process and restored
afterwards so Docker CLI and Testcontainers use the same endpoint.
Conflicting `DOCKER_HOST`/`DOCKER_CONTEXT` and non-loopback Testcontainers host
overrides are refused rather than silently selecting a different runtime.

The pinned `postgres:17.6-alpine` image matches `PostgreSqlFixture`; a missing image
has a bounded 120-second pull. One uniquely named/labeled container is created,
started and checked with `SELECT 1` (60-second readiness allowance, bounded CLI
calls), then its exact published loopback port receives a bounded 3-second TCP
connection check. This probes Docker/SQL/networking, not every Testcontainers/Ryuk
configuration; the actual hosted suite remains mandatory. A temporary password travels only through the owned Docker client's
environment, not argv, files or public output. Cleanup revalidates the full ID
and exact owner label, removes only that container and its anonymous volumes,
then checks absence. Partial startup is also reconciled by exact name/label.
Unknown ownership is never permission to remove a resource.

`safe/preflight.json` keeps prerequisite/primary/cleanup results and owned-server
startup/cleanup timings separate. Primary
failure exits 20; cleanup-only failure exits 21. A later reporting failure never
replaces an existing nonzero primary exit. Full-suite resources remain owned by
the existing Testcontainers fixtures; their cleanup failures remain xUnit
failures. Preflight cleanup success is not a claim about every suite fixture.

| Preflight code | Action |
| --- | --- |
| `linux-required` | Use the hosted Linux job or a Linux Docker workstation. |
| `external-mode` | Remove the four external-ephemeral variables; do not supply a personal database. |
| `sdk` | Install/select the supported `global.json` SDK; prereleases/other feature bands are refused. |
| `runtime` | Install the .NET/ASP.NET Core 10 shared runtimes supplied by the supported SDK. |
| `source-assets` | Use the complete checkout; keep build artifacts under its `.artifacts`. |
| `native-required` | Publish the actual CLI above and set both required-native variables. |
| `temporary-files` | Select an existing writable native Linux temporary location with correct Unix semantics. |
| `capacity` | Provide at least 1 GiB free at source and temporary paths; do not run broad cleanup. |
| `docker-endpoint`, `docker-api` | Start the local Linux Docker engine and correct its Unix socket/context permissions. |
| `postgres-image` | Check access to the pinned image registry; do not substitute an unreviewed image. |
| `docker-create`, `docker-start`, `postgres-ready`, `postgres-tcp` | Inspect private command output and the local loopback mapping; no automatic suite retry is performed. |
| non-`ok` cleanup | Inspect the reported exact owned ID/name before explicit cleanup; never prune globally. |

## Coverage and diagnostics

The baseline at main `275c8aaf7eb47bf91aa1b57fab4fb891a20d18c2` has **862**
discovered rows across 26 classes. Seven new infrastructure diagnostic rows
make this slice's inventory 869. The unmerged #191 inventory of 914 is unrelated.
The checked-in [class map](../../tests/Haby.PostgreSql.IntegrationTests/coverage-map.json)
assigns every class responsibility, actual isolation and the unchanged full gate.
Some existing classes mix responsibilities; the map records their combined
obligations, not a completed project split or permission to share more servers.

`safe/coverage.json` maps **each actual case/theory row** to that class entry.
Identities use the existing exporter formula: SHA-256 UTF-8 of qualified method,
LF and original display name. Parameters stay private. Rows and multiplicity are
retained and compared with the executed canonical inventory; unknown classes,
missing/extra executions and skips fail reporting/nonzero. `complete` means the
inventories match, including failed cases, not that assertions passed. A
`discovered-not-executed` report is incomplete. Class-owned servers do not imply
pristine state per case; existing per-case databases and exclusive login/provider
collections remain unchanged. Later maintenance owns finer isolation/separation.

The existing hosted TLS failure is **not declared fixed**. The unchanged
unauthenticated-status assertion now emits only a structured phase/code on
failure to `safe/host-diagnostics.csv`: host build/start, address scheme/loopback,
channel, reflection write/read, status assertion, or cleanup.
Body failures are captured and recorded before resource cleanup; call, channel and
host cleanup are each attempted in reverse acquisition order. Every cleanup
failure receives its own cleanup diagnostic without a stale body status. The
original body exception retains precedence and its stack; a cleanup-only failure
remains a test failure. Deterministic controls cover synchronous and asynchronous
disposal failure, multiple failing resources and primary evidence before cleanup.
No exception detail, stack,
headers, SQL, credentials or payload is copied. A write-phase `unauthenticated`
result identifies a different failure boundary from an incorrect response status;
it does not prove an environment/root cause or waive the assertion. Investigate
the actual phase before proposing a test fix; production behavior changes require
separate disposition. `HostFailureDiagnosticsTests` checks this closed output.

The explicit upload allowlist follows the safety constraint reviewed in PR #190;
its message-signature parser/schema and the #191 private harness are not imported.

## Windows operator and ordinary evidence

Open the PR's **Checks → Build → postgresql-integration** job, inspect the exact
run/source SHA, then download `postgresql-timing-<SHA>-<attempt>`. The artifact
contains only allowlisted measurement/preflight/coverage/cases/counts/timing/
minimal TRX/Host diagnostic files. `measurement.json` attributes the actual checkout
(which can be the PR merge commit), assembly, SDK, native requirement and phases.
Check the workflow's PR head SHA as well; a different run or old artifact is not
evidence for a new HEAD. Native publish and dependency jobs are separate gates.

Raw TRX/command/discovery logs remain in `private/` (0700 in Docker mode) and are
not uploaded or echoed. Hosted raw data lasts only for that ephemeral runner;
safe Actions artifacts retain 14 days. Locally, keep raw data private only for the
investigation and remove your own completed results directory when no longer
needed. Never replace an earlier failed directory with a later run.

Focused controls: `pwsh -File scripts/test-postgresql-entrypoint.ps1` and
`pwsh -File scripts/test-postgresql-timing-export.ps1`. These use harmless command
boundaries/synthetic results and are not substitutes for the actual hosted suite.
