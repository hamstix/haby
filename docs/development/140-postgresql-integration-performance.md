# PostgreSQL integration timing (issue #140)

The measured change is test-scoped catalog pooling in the exclusive login/access
suite. Two successful Linux baselines averaged 483.65 s for test invocation;
three successful candidates averaged 389.48 s, an observed 19.5% reduction with
all 453 original cases plus three new guards. Windows full runs remain unsuccessful
and are reported separately below; no complete Windows-gate success is claimed.

The baseline starts at `cc16bf01e3745a6e4302f475f1c818ed28172758`,
the merge of PR #139 completing issue #138. Production code, contracts,
models and migrations are outside this performance slice.

## Reproduce measurements

Publish the current Native AOT CLI first, as in the Build workflow. Set
`HABY_AOT_CLI_REQUIRED=1` and `HABY_AOT_CLI_PATH` to that executable.
Do not run builds or another integration invocation concurrently with a measured
run. Use the same machine, configuration, required binary and warm/cold conditions
for both revisions. Use a new result directory on every invocation.

```powershell
# Docker-compatible runtime (the same entry point as Linux CI):
./scripts/test-postgresql-measured.ps1 -ResultsDirectory ./.artifacts/baseline-1

# Windows WSLc: owns one temporary container, never a persistent database:
./scripts/test-postgresql-wslc.ps1 -ResultsDirectory ./.artifacts/baseline-1

# Verify inventory equality and require every case to pass before comparison:
$guards = @(
    'Haby.PostgreSql.IntegrationTests.TestCatalogPoolTests.Catalog_pool_reuses_reset_sessions_and_bounds_waiters'
    'Haby.PostgreSql.IntegrationTests.TestCatalogPoolTests.Cleanup_closes_owned_idle_and_returned_sessions_without_clearing_another_pool'
    'Haby.PostgreSql.IntegrationTests.TestCatalogPoolTests.Pool_budget_and_disposed_owner_refuse_more_work'
)
./scripts/compare-postgresql-timing.ps1 `
    -Before ./.artifacts/baseline-1/safe -After ./.artifacts/candidate-1/safe `
    -AdditionalMethods $guards

# Measurement safety checks (no PostgreSQL needed):
./scripts/test-postgresql-timing-export.ps1
```

The measured runner executes the entire unfiltered integration project, separating
restore, build, test invocation and export wall clocks. The test invocation includes
discovery, fixture lifecycle and runner overhead. TRX records actual test start/end
times and durations. `setup.csv` records cluster readiness/cleanup, operator catalog
creation and the common operator Host migration/start helper. These setup samples
are nested in test/fixture work: do not add them to suite elapsed time or mistake
summed parallel durations for the critical path. Some tests call their own migration
or Host setup directly; the common-helper sample is not a complete migration total.
Image download/readiness is included in fixture readiness in container-owned mode.
In WSLc mode container launch/stop also surrounds the measured runner. Native publish,
SDK installation and artifact upload remain separate CI steps with their own Actions
wall clocks; they are not attributed to test execution.

## Artifacts and failures

Only the `safe` directory is published, on success and failure. It contains
`measurement.json`, allowlisted `results.trx`, `cases.json`, top twenty durations,
class duration/span summaries and numeric setup timings. Original parameterized
case names are represented by SHA-256 of qualified method plus original case name;
distinct parameter cases retain distinct stable identities. The method name remains
readable. No parameter text, assertion messages, stdout, exception details, file paths
from TRX or attachments are copied. Raw TRX/build/test logs stay under `private`
and must never be uploaded wholesale. Export rejects duplicate execution, missing
definitions, counter mismatches and unsupported outcomes. XML DTDs are prohibited
and raw XML input is limited to 64 MiB. A failed export fails an otherwise successful
run; a failing test's original nonzero exit code survives export and manifest/output
failures. Native command error-action preferences cannot intercept that exit code.

The WSLc helper restores temporary environment settings and stops exactly its own
container in `finally`. Cleanup failures are reported separately. Existing database,
role and private dump/archive ownership remains unchanged.

## Source-bound evidence

Investigation and full-run results are recorded here as they complete. No speedup
or full-suite correctness claim is made from an incomplete or failed run.

| Run | Source/test behavior | Helper wall | Cases | Outcome |
| --- | --- | ---: | ---: | --- |
| Windows baseline 1 | `cc16bf01e3745a6e4302f475f1c818ed28172758`, unchanged | 634.91 s | 453 | 451 passed, 2 failed, zero skipped |
| Windows baseline 2 | `5ff73a2fd9a7626d9674af423de498a1fcf6aae0`, measurement only | 638.79 s | 453 | 446 passed, 7 failed, zero skipped |
| Linux CI baseline 1 | `5753fc6841f6bdab65edd39ead71503bb882702a` (PR merge ref for `5ff73a2`) | 516.86 s (runner) | 453 | all passed, zero skipped |
| Linux CI baseline 2 | `53c787cbc7267b63d165c5b20e8f199155afdb65` (PR merge ref for `9254f56`) | 504.36 s (runner) | 453 | all passed, zero skipped |
| Windows candidate 1 | `42a8b44aea379e52fe13bb4200bf6c315b57bdc0` | 472.83 s | 456 | 452 passed, 4 failed, zero skipped |
| Windows candidate 2 | `465c2d29266d68e2cc3e268e2ba3c152fcaa1b55` | 485.28 s | 456 | 455 passed, 1 failed, zero skipped |
| Linux CI candidate 1 | `930836bf38c8e2b8c621f4614bb8ae4f9a3ad536` (PR merge ref for `42a8b44`) | 405.55 s (runner) | 456 | all passed, zero skipped |
| Linux CI candidate 2 | `51433c3e45529e7c6dbd39ddfcb75dab2965af03` (PR merge ref for `c86c939`) | 413.86 s (runner) | 456 | all passed, zero skipped |
| Linux CI candidate 3 | `9b4fd8ee416640b0ebeedd8888d3c707edb763a9` (PR merge ref for `465c2d2`) | 424.10 s (runner) | 456 | all passed, zero skipped |

Environment: Windows 11 build 26200, .NET SDK 10.0.402, runtime 10.0.13,
Debug, WSLc 3.0.3, PostgreSQL 17.6-alpine, required published win-x64 Native AOT
CLI. First integration build was cold in this worktree; native publication preceded
the timed helper. Other unrelated host activity was not controlled. The TRX runner
reported 10m02s. In the test timeline the operator class (81 cases) spanned 298.59 s
and ended the normal parallel phase, followed by the exclusive login/logging class
(82 cases, 268.52 s). Remaining exclusive Provider collections completed afterward.
These sequential segments, not the sum of every class duration, explain the
critical path. Largest individual cases included metadata pagination (26.49 s),
Profile boundary/native round trips (22.11 s) and five-step native paths (18.52 s).

The two failing cases were a five-step parameter case and
`Unavailable_pinned_secret_access_is_retryable_with_retained_attempt_history`.
One returned the safe registry-unavailable gRPC outcome; the other exposed a Windows
socket-address-exhaustion exception while opening PostgreSQL. No tests were retried
or removed. Sampled TIME_WAIT counts reached 8,039; a later container sample showed
7 sessions, 464 databases and max_connections=100 (samples, not measured peaks).
This failed run establishes connection churn as a Windows investigation lead,
not a successful baseline or a Linux performance result.

Windows baseline 2 separated restore 1.09 s, build 9.79 s, test invocation 609.70 s
and artifact export 0.36 s (runner total 621.21 s, helper total 638.79 s).
Its full identity inventory exactly matches baseline 1. Socket exhaustion recurred;
the additional failures are retained. The assembly was built from clean `5ff73a2`
before candidate edits; its SHA-256 was captured independently while running.
The initial runner captured the worktree diff at the end, so baseline 2's diff hash
includes subsequent uncompiled investigation edits. Later runner versions capture
source/diff and assembly hash before execution and flag source drift explicitly.

Linux baseline 1 is [Build #259](https://github.com/hamstix/haby/actions/runs/42224403732),
with its [allowlisted artifact](https://github.com/hamstix/haby/actions/runs/42224403732/artifacts/13505088230).
It used a fresh Ubuntu 26.04.1 Actions runner, four CPUs, 15.61 GiB RAM,
SDK 10.0.403, runtime 10.0.14, Docker 29.4.2 and required linux-x64 Native AOT.
Restore/build/test/export were 4.94/22.11/488.91/0.72 s. Native publication and
artifact upload remain separate steps. All five CI jobs passed. These runner
resources and SDK differ from Windows; timings must not be compared across OSes.

Checked-in allowlisted [CSV inventories and run summaries](evidence/140/) retain
every case identity/outcome/duration beyond CI artifact expiration. Parameters remain
hashed. [run-summary.json](evidence/140/run-summary.json) retains exact branch/merge
SHAs, assembly hashes where captured, phase times, class spans, outcomes and CI
artifact digests. Downloaded artifact ZIP digests were verified before reconciliation.

### Comparable Linux results

All five runs used Debug, SDK 10.0.403, the four-CPU Ubuntu 26.04.1 hosted-runner
class, unchanged xUnit CPU-bound scheduling and required published linux-x64 Native
AOT. Each used a fresh Actions VM with no repository build cache. They were separate
VMs, not a controlled physical host: storage/image/readiness variability and xUnit's
default collection order remain limitations. No cross-OS speedup is inferred.

| Run | Restore | Build | Full test invocation | Export | Runner total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline 1 | 4.94 s | 22.11 s | 488.91 s | 0.72 s | 516.86 s |
| Baseline 2 | 4.39 s | 20.36 s | 478.39 s | 0.50 s | 504.36 s |
| Candidate 1 | 4.62 s | 17.82 s | 382.16 s | 0.54 s | 405.55 s |
| Candidate 2 | 4.43 s | 22.39 s | 385.85 s | 0.45 s | 413.86 s |
| Candidate 3 | 3.61 s | 19.10 s | 400.41 s | 0.51 s | 424.10 s |

Mean full test invocation fell from 483.65 s to 389.48 s (19.47%). Mean runner total
fell from 510.61 s to 414.50 s (18.82%). Native publication, SDK/runtime installation
and artifact upload are excluded from these runner totals and remain distinct CI
steps. These are observed samples, not a guarantee or a statistical significance
claim. The first paired two-candidate comparison was 20.6%; including the third
candidate gives the more conservative reported mean above.

The exclusive login/access class spans were 183.74/177.49 s before and
71.48/76.17/77.74 s after. In baseline 1/2 and candidate 1, the normal parallel
phase ended about 260/238/257 s after the first recorded case. The operator class's
own start time changed with collection scheduling; its shorter isolated duration
must not be credited as a separate optimization. Pooling the subsequent serial
login phase explains the demonstrated critical-path gain. The measured common
operator catalog-create and Host-start/migrate helpers were a small fraction of
its elapsed span, so template cloning was not selected.

Linux fixture readiness samples were 18 fixtures before and 19 after, reflecting
the three guard cases' additional class fixture. Readiness samples overlap, so their
sums are resource observations rather than suite elapsed time. Artifact archives
were about 117-139 KiB; final-format export took roughly half a second. CI still has
the same five jobs and no shards. Investigation repeated full workflows for evidence;
ordinary PRs execute one complete gate.

### Windows results and limits

Candidate 1/2 test invocations were 447.36/460.07 s, runner totals 454.52/467.51 s,
and helper totals 472.83/485.28 s. Both executed every original case and all three
guards; both passed all 82 login/access cases and all guards. The login class span
was 119.80 s in candidate 1, versus 268.52 s in baseline 1. These observations are
from unsuccessful full runs and are not a successful Windows speedup/parity claim.

Candidate 1 retained four failures in the unchanged normal parallel phase, including
socket exhaustion and unavailable-operation outcomes. Candidate 2 retained one
five-step case returning the safe registry-unavailable gRPC response; its root cause
is not confirmed. The first candidate flagged source drift because subsequent
script/documentation commits were made while its unchanged C# assembly ran. The
second candidate used clean `465c2d2` throughout and explicitly recorded no source
drift. No failed case was retried to replace these outcomes.

A candidate-1 spot sample during login work showed 15 total server sessions
(including the observer) and two login-catalog sessions, not measured peaks.
All helper-owned containers were stopped; a final WSLc inventory contained no
`haby-pg-test-*` containers. No host socket settings or persistent developer resources
were changed. A clean complete Windows WSLc result remains unproven.

## Selected candidate

The exclusive `PostgreSqlApplicationLoginTests` class is a measured serial segment
on both platforms (Windows baseline 1: 268.52 s; Linux baseline 1: 183.74 s).
Only its ordinary catalog setup now registers per-case pools. Each pool has a
minimum of zero and a maximum of eight physical connections; each case can register
at most four distinct pools (a hard ceiling of 32 catalog connections for this
exclusive class, normally one pool). xUnit disposes the owner after every case,
closing idle sessions and marking borrowed sessions for closure on return. Normal
session reset remains enabled. The existing one-connection-pool case retains its
one-connection limit and uses targeted cleanup instead of `ClearAllPools`.

Provider/application authentication, generated-password verification, fault hooks,
restore targets, truly empty catalogs and every migration invocation are retained.
The normal parallel operator class and all collection scheduling are unchanged.
No template clone, new dependency or CI shard is introduced. The lifecycle guard
class adds one short-lived fixture container on Docker; WSLc reuses the helper's
cluster. The optimization itself retains the existing cluster ownership. Tradeoff:
a case can retain idle PostgreSQL backends until its deterministic teardown, in
exchange for fewer physical connections/authentication handshakes. This is a
test-only lifecycle; no production connection policy changes. Clearing closes
physical sessions; Npgsql's connection-string pool metadata can remain until the
test process exits. No managed-memory reduction is claimed.

Three new full-suite guard cases check session reuse/reset, the eighth-connection
bound and cancellable ninth waiter, owned idle/borrowed cleanup, an independent
pool's survival, pool-budget refusal and disposed-owner refusal. A Windows WSLc
focused run passed all three plus the existing single-pool final-action case
(4 passed, zero failed/skipped; 6 s runner duration). Subsequent complete inventories
prove all three guards execute alongside all existing cases, without filters,
retries, duplicate identities or skips.

The first Windows baseline uses the unchanged post-#138 test assembly and original
helper, adding only explicit raw TRX arguments in a local ignored helper copy.
It requires the freshly published Windows Native AOT binary. Later measured runs
use the checked-in runner; compare the separately reported build/test phases, rather
than treating the first run's cold build as a candidate test-execution improvement.

## Isolation constraints

Existing `Provider alias isolated` and `Login logging isolated` collections disable
parallel execution. Preserve them: global role/database count assertions and
`ALTER SYSTEM log_statement`/server-log checks require exclusive cluster access in
the Windows shared-cluster mode. Unique database names alone do not isolate these
effects. Other cases use unique database/role names, local fault hooks and independent
DbContexts. Migration, obstructed/fresh startup, current-build dump/restore, recovery,
fault/uncertainty and required published Native AOT paths remain in the full project.

## Validation and CI

Local solution restore/build/test/format and `git diff --check` passed. Solution
tests: 944 passed, zero failed/skipped, with the published Windows Native AOT binary
required. Local solution and integration builds had zero warnings/errors. The
Windows Native AOT publish used warnings as errors. The final candidate's measured
Linux build reported no compiler warning codes. Actions emitted unrelated Node.js
deprecation notices; those are not compiler/AOT warnings.

All original real PostgreSQL, empty/prior-version migration, restore, fault,
uncertainty, recovery and actual Host/generated-client/published-CLI cases remain in
the inventories. Persisted arrange/act/assert contexts and all existing assertions
are unchanged. There are no production/model/migration/package changes; additional
EF model CLI checks were not run because no model or migration file changed.

The two baselines' PostgreSQL jobs passed 453/453. The three candidates' PostgreSQL
jobs passed 456/456. [Build #262](https://github.com/hamstix/haby/actions/runs/42226969978)
and [Build #263](https://github.com/hamstix/haby/actions/runs/42227357171) passed all
five jobs, including both Windows/Linux Native AOT publishes and all 15 loopback
cases on each platform. Build #260/#261 had a measurement-self-check step failure:
its assertions passed but the script retained the intentionally tested child exit
17. That shell-exit defect was fixed and the separate-process self-check now exits
zero after success. Those historical failures remain visible; the real PostgreSQL
measurements from those runs were successful and their artifacts are retained.

The complete Linux inventory and Windows observations support this scoped test
lifecycle change. The complete Windows gate still has the explicitly retained
failures above. This report does not claim an independent review or a clean Windows
full-suite result.
