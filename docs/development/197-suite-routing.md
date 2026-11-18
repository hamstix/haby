# Functional suites and infrastructure qualification

> Accepted amendment: [ADR-061](../adr/0061-focused-development-and-final-qualification.md)
> separates focused iteration from exact-SHA full merge qualification in #263,
> after #259 and before #261. Earlier every-change/full-gate wording describes
> current execution until that safe mechanical gate transition is implemented.
> Full applicable qualification before merge remains mandatory; this documentation
> changes no workflow, required context or remote protection setting.


Issue #237 declares 1759 fast, 1148 PostgreSQL total, 104 native cases per platform
and 24 configured-consumer assertions. Its additions are 17 fast, 27 functional,
10 native per platform and A01-A03 in the existing configured gate. The configured
renewal assertions run before Applied and C11 now verifies retained completion,
publications and identities. These inventories are not executed passing evidence;
see the [initial completion guide](237-initial-retirement-applied.md).

Issue #235 adds 32 fast rows, eighteen functional PostgreSQL rows, seven published-native
rows per platform and four configured-consumer API rows for bounded batch readiness.
The current ledger declares 1742 fast, 1121 PostgreSQL total, 94 native rows per
platform and 21 configured-consumer assertions (C01-C11, R01-R06 and B01-B04).
These are required inventories, not a transfer of historical passing evidence;
see the [batch observation contract](235-batch-readiness-wait.md).

## Pre-routing obligation map

This map is recorded before changing routing, from main
`7f655c56f5279eeabe0535eb38dba72daea25f7c`, actual run 381 PostgreSQL
identities and clean-checkout fast discovery. The checked-in
[case/parameter ledger](../../tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json)
retains every row, hashed theory identity and platform/gate obligation.

| Existing obligation | Cases | Destination | Required trigger/gate |
| --- | ---: | --- | --- |
| Product PostgreSQL/Host/E2E and regular deterministic controls | 880 | `functional` | Every change, `postgresql-integration` |
| Owned Docker server capacity, isolation and startup/body-failure cleanup | 4 | `infrastructure-qualification` | Every-change `full` plus infrastructure changes, weekly/manual qualification |
| Existing solution tests | 1299 | `fast` | Every change, `validate` |
| Published CLI loopback rows | 75 per platform | Native win-x64 and linux-x64 | Every change, existing `Native AOT (win-x64)` / `Native AOT (linux-x64)` |

The 75 loopback identities are also exercised by ordinary solution tests; their
published-native Windows/Linux executions are distinct obligations, not duplicate
coverage substituted for one another. Only four actual infrastructure-only cases
are assigned qualification. Product fault/recovery, restart, restore, TLS,
authorization, concurrency and secret behavior remain functional regardless of
expense. Fast safety controls remain regular.

The regular required PostgreSQL job retains **full** qualification. The four
controls took a 9.21-second class span in run 381 and protect the resource lifecycle
just introduced in #195. Removing them from regular CI would reduce that existing
safety gate for negligible demonstrated savings. Explicit selection and a
weekly/manual qualification path do not weaken the current required gate. Relevant
infrastructure changes are already covered by the every-change full gate, so a
second PR qualification run would be redundant. No PR duplicates are planned.

The default `full` must execute both disjoint PostgreSQL suites exactly once and
aggregate discovery/execution Counters. Explicit `functional` or
`infrastructure-qualification` selections must report selected-suite completeness,
not full-suite success. Invalid/empty selections and empty groups must fail.

## Dependency and isolation audit

Issue #233 adds 15 fast identities, four PostgreSQL functional identities and nine
published-native identities per platform, plus native public wait/resume assertions
in the existing configured gate. Existing submission rows now verify separate public
wait/replay. The ledger declares 1710 fast, 1103 PostgreSQL total and 87 native rows
per platform. These are executable obligations, not passing evidence. No consumer
image input or infrastructure-qualification obligation changes.

Issue #223 adds 55 fast rows, 12 PostgreSQL functional rows and 11 assertion
rows in the opt-in runnable consumer gate, with no added infrastructure-qualification
rows. Two fast identities are explicitly replaced for the new invalid intent
version and verified-preparation test name; the ledger records their previous
case hashes. Issue #225 adds 65 fast identities, 18 PostgreSQL functional identities
and R01-R04 in the same configured gate, preserving C01-C11 and existing cleanup.
The ledger now declares 1670 fast and 1078 PostgreSQL total (1073 functional + 5
qualification); final hosted discovery/execution must match every row. These are
inventory counts, not executed success. The configured consumer retains its own
production-projected real-cluster evidence. Both existing Native AOT jobs retain
their required publications/tests and record the actual SDK/compiler packs;
the .NET 10 compiler must meet the patched 10.0.14 baseline without audit suppression.

Host-bearing classes include Configuration/Generation scenarios, Operator Host,
checkpoint RPC, Provider preflight/access and loopback Host configuration. They
retain real generated clients, native CLI where required, dedicated Provider-effect
servers, exact authorization and all product assertions. The three global Npgsql
logging rows retain their exclusive collection. Existing Windows CLI/filesystem
branches and native Unix temporary-permission checks are not relocated or relaxed.

Architecture tests inspect actual project XML through an explicit validated source
root. JSON schema/corpus consumers need data rather than checkout inspection and
will use copied test assets. Neither group should depend on walking parents of
the build-output directory. Existing Docker fixture lanes and lifecycle ownership
remain; heavy Host group concurrency must have an explicit validated limit using
the same non-waiting collection mechanism.

## Running and selecting suites

The first table describes the unchanged baseline. This slice adds nine regular
source/asset controls, eight regular Host-bound layout/value controls and one
real heavy-group capacity qualification control. Inventories at #197 are **1308
fast**, **888 functional**, **5 infrastructure qualification**, and **75 published
native rows per platform**. The ledger marks added rows with `addedFor197`; none
of the 884 PostgreSQL or 1299 fast baseline rows is removed or renamed.

The subsequent #188 anonymous-reflection assertion correction adds seven regular
deterministic controls (`addedFor188`). Current PostgreSQL inventory is **895
functional + 5 qualification = 900**; fast/native inventories are unchanged.

For normal source inspection on Windows PowerShell, from the checkout root:

```powershell
$env:HABY_TEST_SOURCE_ROOT = (Get-Location).Path
dotnet restore ./HamstixHaby.slnx
dotnet build ./HamstixHaby.slnx --no-restore
dotnet test ./HamstixHaby.slnx --no-build
```

On Linux, use `export HABY_TEST_SOURCE_ROOT="$PWD"` before the same solution
commands. Project XML architecture tests reject missing, relative or incomplete
roots rather than searching output parents. Schema, corpus and profile-example
JSON is copied into each consumer's `TestAssets`; missing files/empty example
directories fail. No source root is needed merely to read those copied assets.

Publish the required native CLI using the [existing clean-checkout command](193-hosted-postgresql-entrypoint.md),
then select PostgreSQL suites explicitly:

```sh
pwsh -File scripts/test-postgresql-measured.ps1 -Docker -Suite full \
  -ResultsDirectory .artifacts/pg-full
# Explicit partial runs, with fresh result directories:
pwsh -File scripts/test-postgresql-measured.ps1 -Docker -Suite functional \
  -ResultsDirectory .artifacts/pg-functional
pwsh -File scripts/test-postgresql-measured.ps1 -Docker -Suite infrastructure-qualification \
  -ResultsDirectory .artifacts/pg-infrastructure
```

`full` is the default and means both **PostgreSQL** suites; the fast and two native
gates are separately mandatory in Build. All modes retain SDK/runtime/native,
Docker, source/assets and native Unix temporary-file preflight. The runner pins
`HABY_TEST_SOURCE_ROOT` to its actual checkout only for its child execution and
restores the caller's value. Empty/unknown suite names or empty partitions fail
before execution. Partial selection requires Docker; legacy external mode gains
no inferred isolation or filtered-success claim.

The full assembly is discovered and checked against the complete ledger first.
Each selected filter's actual discovery must match its mapped Counter. Functional
selection excludes the qualification classes instead of including only a known
functional whitelist: an uncategorized new test cannot silently disappear from
full execution. Its unmapped identity fails discovery/accounting. Filters use the
[documented VSTest operators](https://learn.microsoft.com/en-us/dotnet/core/testing/selective-unit-tests?pivots=xunit).

Each disjoint suite executes once with `--no-build --no-restore` against the same
test assembly and required native executable. Restore/build are shared, not
duplicated. Each suite gets its own owned test process, resources and result
directory. Full aggregation checks the exact union against discovery, including
theory multiplicity. `coverage.json` says `complete` only for full PostgreSQL;
partial commands say `selected-complete` and name their selection. Completeness
is inventory accounting, not a waiver of failed assertions or nonzero exits.

A test failure with confirmed cleanup permits the next requested suite to run;
its original nonzero exit survives later success or reporting failure. Missing
TRX, failed accounting or unconfirmed cleanup fails and leaves remaining suites
explicitly unperformed, never silently skipped success. There is no automatic
test retry. Raw logs/TRX remain under `private/`; all published files are explicit
allowlist entries.

## Heavy Host group bound

`HABY_TEST_POSTGRESQL_SERVERS` remains 2–8, default 4. The additional
`HABY_TEST_HOST_GROUPS` is 1 through **server limit minus one**, default
`min(2, server limit minus one)`. Invalid values fail preflight; no fixture waits
for a permit. Configuration, Generation, Operator Host and checkpoint classes use
dedicated `HostPostgreSqlFixture` instances and only the first configured Host
lanes. Other owned classes may share those sequential lanes, not their servers.
Exclusive Provider/logging collections retain their existing isolation; their
Host scenarios run alone. The light, database-free Host configuration collection
retains ordinary xUnit scheduling.

This bounds simultaneous **heavy fixture groups**, not the intentional clients,
hosts or native processes inside a product concurrency scenario. Their existing
assertions and concurrency are unchanged. A fail-closed group reservation catches
an unexpected extra owner before container creation. The new actual Docker
control starts the configured number of such fixtures, checks real SQL, refuses
an extra owner without waiting, and releases all owners. The 10-second guard on
that new negative control detects a waiting regression; no existing product or
global timeout is increased.

## CI, qualification triggers and evidence

### Documentation-only Build routing

Issue #253 accepts a narrow exception to the every-change obligations below.
Build always runs `validate`, starting with the dependency-free Node controls
in `scripts/test-ci-change-routing.mjs` and `scripts/ci-change-routing.mjs`.
No branch spelling is trusted. The allowlist is root `README.md`, `ROADMAP.md`,
`CONTRIBUTING.md`, `AGENTS.md` and ordinary `docs/**/*.md`. Any `examples`,
`schemas` or `fixtures` subtree under `docs` is excluded, including Markdown.
Only regular non-executable files qualify. Unknown/mixed paths, scripts,
workflows, code, examples, schemas and fixtures retain full validation.

The source-consumer audit at `f12f0dd723ca699ad730815e3059ccac7b5f14f1`
found these functional inputs under `docs`:

- `docs/development/examples/106-shared-application-login.json` is embedded by
  `Haby.PostgreSql.IntegrationTests.csproj`.
- `docs/examples/configuration-profiles/*.json` is copied by
  `Haby.Manifest.Tests.csproj` and read by `ProfileWireTests`.
- `docs/development/schemas/postgresql-failure-evidence-v1.schema.json` is read
  by `scripts/test-postgresql-failure-evidence.ps1`.

No allowed Markdown input was found in source, project, test or script consumers.
The fixed consumer image input closure excludes Markdown and includes none of
these documentation paths. New test/runtime consumers must update the routing
exclusions with their change. Kubernetes workflow path filters remain unchanged;
this change does not rebuild or requalify the consumer image.

Checkout fetches full history. PR routing uses the unique merge base of the event's
base/head commits through its entire head, including every commit. Push routing
uses the entire event `before..after` range. Git raw NUL-delimited records retain
all filenames, deletions, file modes and both rename paths. A rename across the
allowlist boundary takes full CI. There is no API pagination or shell evaluation
of filenames. Missing commits, shallow history, ambiguous ancestry, malformed or
incomplete records, invalid UTF-8, failed Git commands and exceeded output limits
fail `validate`; no partial output can authorize skipping. An empty diff takes
full CI. A complete reliable non-documentation classification takes full CI.

Documentation checks run on every route. `git diff --check` checks added whitespace
defects across the complete range. The link check compares all tracked Markdown
at both endpoints, so deleting/renaming a target can fail incoming links even in
unchanged files. It checks local inline/image links and reference definitions,
including encoded paths and directory links. Existing unresolved links are a
counted baseline, not new failures; new unresolved occurrences fail. Fenced and
inline code, external URLs and fragments are ignored. This is a practical local
path check, not a complete Markdown parser, anchor validator or network crawler.
Run the controls locally with `node --test ./scripts/test-ci-change-routing.mjs`.

Only successful routing/documentation validation emits `full=true` or `full=false`.
On docs-only changes, `validate` skips SDK setup, restore/build, solution/fast tests,
formatting, dependency audit and the heavy script controls; its routing controls
and documentation checks remain mandatory. Its summary explicitly records the
source range, route and omitted suites. PostgreSQL and the two Native AOT jobs
use `needs: validate` and require `full == 'true'`. A failed `validate` remains
failed even though dependent jobs skip; there is no success aggregator overriding
it. Full changes preserve every existing applicable command, inventory and artifact.
Dependency review retains its independent PR-only behavior and read permissions.

Required names remain `validate`, `postgresql-integration`, `Native AOT (win-x64)`,
`Native AOT (linux-x64)` and `dependency-review`; no protection settings change.
The former native matrix is represented by two jobs with literal display names
and shared YAML-anchored steps/defaults. Job conditions run before matrix expansion;
fixed names preserve both platform checks even when skipped. Both platforms still
use their original runner, RID, commands and independent failure behavior.
GitHub supports skipped jobs as successful required checks, while failed required
`validate` blocks merge; see the official
[job conditions](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-jobs-with-conditions)
and [prerequisite jobs](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-jobs).
Intentionally skipped suites earn no case, coverage or artifact credit.

This workflow-change PR itself takes the full applicable path. Focused controls
and static inspection do not prove hosted docs-only execution. A useful follow-up
documentation PR after #253's normal merge must record its exact source/diff,
actual `validate` and dependency-review outcomes, and actual PostgreSQL/native
skips. That rehearsal is pending until executed; it is not merged automatically.
Current-SHA independent review, architect approval and the existing failure STOP,
no automatic rerun and evidence rules remain required.

### Full and qualification paths

Existing check names remain `validate`, `postgresql-integration`, both Native AOT
targets and `dependency-review`. The PostgreSQL job explicitly runs `full`; this
executes the four moved and one new qualification rows in this PR as well as all
functional obligations. Infrastructure changes therefore always trigger the
qualification destination without a duplicate PR job.

The additional **Infrastructure qualification** workflow runs on Monday at
07:23 Moscow time (04:23 UTC), or through **Actions → Infrastructure qualification
→ Run workflow**. Maintainers own failures and any explicit cleanup. It selects
only qualification and reports partial-scope completion; it is not a replacement
for Build. The scheduled/manual workflow becomes available on main after merge;
the required full job supplies this PR's actual qualification execution evidence.

Windows contributors can inspect Build and download the exact source/run
artifacts without a local full PostgreSQL bridge. The PostgreSQL artifact contains
root aggregate files and per-suite case/count/resource/setup files. Metadata
records the shared restore/build phase and each suite's discovery/test/export
durations. Per-suite setup CSV separates server-ready/cleanup and existing Host
startup/migration events; these events do not cover every Host start and no-op
disposal/negative initialization events are not actual container counts. Resource
JSON separately records authoritative fixture counts, ready/reserved peaks,
Host-group limit/peak, active owners and cleanup outcomes. Peaks aggregate by
maximum, counts by sum; overlapping class spans are not additive.

Fast and published-native gates now publish their own minimal case/count/TRX and
source/dirty/native-hash metadata, checked against the same ledger. Some fast
theory rows have identical display hashes: multiplicity is retained and a separate
hash of the actual opaque test ID distinguishes row executions. Exact repeated
executions remain invalid, including duplicates across TRX inputs. This avoids
deduplicating different parameter rows just because their displays are identical.

The shared exporter also publishes [bounded failure evidence](188-safe-failure-evidence.md)
for each suite and aggregate, preserving exact failed-row joins. Parsed signatures
are untrusted text categories, never runtime type, phase or root-cause evidence.

No XML output, exception messages/stacks, attachments, SQL or raw theory values
are copied into public artifacts. Existing 14-day safe retention and ephemeral
private hosted logs apply. Local raw evidence stays private and is cleaned only
by its owner when no longer needed. Focused controls are
`test-postgresql-entrypoint.ps1`, `test-postgresql-timing-export.ps1` and
`test-postgresql-suites.ps1`; they remain regular and do not substitute for actual
hosted functional/qualification runs.

## Historical limits

Run 381 measured 1601.13 seconds in tests, slower than run 379's 1195.59 seconds.
Its four-server peak was validated, with 27 starts/cleanup and 93 shared-case
database create/drop pairs. These are reference observations, not a speed target
or a causal comparison. No statistical or tuning reruns are part of this slice.

The WSLc helper still has documented callers in the PostgreSQL, local validation,
active restore and alpha operator guides. Its external shared-server semantics
cannot replace fresh-server qualification. It remains explicitly legacy; no
execution path is removed without replacement evidence. No #191 harness is used.
After this slice the architect must explicitly reassess #188/#191; no old run chain
or functional P3 work resumes automatically.

Issue #202 adds twenty-one fast and twenty-three functional PostgreSQL rows, plus thirteen
scoped real API/catalog rows in `kubernetes-durable-actions` (`api-catalog`). The
current ordinary inventories are 1411 fast and 923 PostgreSQL (918 functional,
5 qualification); native and existing 24 kernel rows are unchanged. The new
[internal action guide](202-durable-workload-intents.md) describes the bounded
launcher mode. Existing case identities and platform obligations are retained.
