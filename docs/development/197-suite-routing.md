# Functional suites and infrastructure qualification

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
real heavy-group capacity qualification control. Current inventories are **1308
fast**, **888 functional**, **5 infrastructure qualification**, and **75 published
native rows per platform**. The ledger marks added rows with `addedFor197`; none
of the 884 PostgreSQL or 1299 fast baseline rows is removed or renamed.

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
