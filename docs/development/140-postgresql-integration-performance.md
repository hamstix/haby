# PostgreSQL integration timing (issue #140)

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
./scripts/compare-postgresql-timing.ps1 `
    -Before ./.artifacts/baseline-1/safe -After ./.artifacts/candidate-1/safe

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
run; a failing test's original nonzero exit code survives either export outcome.

The WSLc helper restores temporary environment settings and stops exactly its own
container in `finally`. Cleanup failures are reported separately. Existing database,
role and private dump/archive ownership remains unchanged.

## Source-bound evidence

Investigation and full-run results are recorded here as they complete. No speedup
or full-suite correctness claim is made from an incomplete or failed run.

| Run | Source/test behavior | Helper wall | Cases | Outcome |
| --- | --- | ---: | ---: | --- |
| Windows baseline 1 | `cc16bf01e3745a6e4302f475f1c818ed28172758`, unchanged | 634.91 s | 453 | 451 passed, 2 failed, zero skipped |

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
