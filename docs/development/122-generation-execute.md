# Explicit generation execution

Issue #122 exposes the existing `DurableGenerationRunner` through internal alpha
`ExecuteGeneration(GenerationRequest)` and:

```text
habyctl deployment generation execute <deployment-id> <generation>
habyctl deployment generation get <deployment-id> <generation>
```

Follow the [production rehearsal](120-five-step-operator-path.md) for bootstrap,
import, explicit Plan/Check/Commit, execution and independent target verification.
The five manual operation selections were error-prone operator work; the new
command moves that sequential pass to the existing server runner. It does not
combine acceptance with execution. One Application per ChangeSet, explicit exact
generation 1, frozen dependencies/inputs and exact handler support remain required.

Host injects the production runner and shares the singleton
`DurableOperationSerialization` with operation execute. The runner owns the gate
and invokes the executor's core without a nested lock. Progress reads use the
journal without taking the execution gate. No new schema, migration, catalog reset,
package, Provider effect or recovery capability is introduced.

## Progress and outcome contract

`generation get` preserves the immutable desired snapshot and separately returns
persisted progress. Both paths use the same server projection: exact deployment
and generation, desired/applied generation, completed/total, bounded nodes with
existing operation metadata, state, reason, last attempt outcome and blocking
dependencies. The graph already limits progress to 256 nodes. No action inputs,
output payloads, credentials, verifier or connection string are projected.
Missing applied generation or attempt outcome is absent, never an invented zero
or success. An unfinished attempt and an explicit attempt outcome remain distinct.

Progress's optional `stoppedOperationId` is derived from persisted failure,
uncertainty or closed failed/canceled pre-dispatch attempt. Execution also returns
its optional stopped operation and explicit `stopOutcome` when the runner supplied
one. Cancellation while waiting for the gate can have neither. Handler/input
refusal without a journal transition is visible in that execution response;
subsequent reads report the actual unchanged journal, not a fabricated stored
execution outcome. Blocking dependencies are unfinished dependencies, not proof
that an operation can otherwise execute.

| Execution envelope outcome | Exit | Meaning |
| --- | --- | --- |
| `succeeded` | 0 | Every mandatory node succeeded and this exact generation is applied |
| `invalidRequest` | 1 | Invalid exact deployment ID or nonpositive generation |
| `unsupportedGeneration` | 1 | Valid generation outside current generation-1 admission |
| `notFound` | 1 | No committed exact generation/operation |
| `blocked` | 1 | No eligible ready work or aggregate completion not confirmed |
| `failed` | 1 | Definitive failure; invocation stopped |
| `retryableFailure` | 1 | Safe pre-dispatch failure; invocation stopped, no automatic retry |
| `needsAttention` | 1 | Retained uncertainty; no redispatch or new recovery |
| `dispatchUncertain` | 1 | Dispatch confirmation uncertain; inspect, no automatic retry |
| `inputUnavailable` | 1 | Exact saved inputs unsupported/unavailable; no substitution |
| `handlerUnavailable` | 2 | Required exact execution/output support unavailable |
| `canceled` | 3 | Runner observed cancellation; progress may include dispatched uncertainty |

Parsing/configuration failures remain exit 2; transport/deadline/cancellation or
unreadable response remains exit 3 with a safe stderr inspection instruction.
A lost/canceled RPC is not guaranteed to deliver a typed reply. In that case stdout
has no fabricated execution envelope. A received typed outcome, including canceled,
is exactly one JSON envelope plus newline; text includes incomplete/stop wording
and the read-only inspection command. Only a fully checked success exits 0.
`generation get` remains exit 0 for found progress even when execution is incomplete.

After a partial stop such as completed 1/5, AppliedGeneration stays absent and later
nodes remain Pending. Inspect with generation get and the exact operation get.
After restoring the specific safely retryable pre-dispatch cause, a new explicit
execute resumes without repeating the first success. After uncertainty, repeat
execution only observes/refuses retained uncertain work; it does not repair it.
Lost responses require read-only inspection before deciding anything further.
Standalone database-create assignment remains standalone-only. Pair assignment,
role/access repair, recovery retries, replan, background work and rotation remain
deferred. Single-instance loopback/TLS, isolated PostgreSQL 17/SUPERUSER,
Plaintext catalog/backup exposure and logging restrictions still apply.

## Reproducible validation

- `CliGenerationLoopbackTests.cs`: one generated RPC per command, all typed
  outcomes/exits in text/source-generated JSON, no CLI operation loop or transport
  retry, lost/canceled response inspection guidance, false-success rejection.
  The existing Windows/Linux Native AOT CI filter includes these tests.
- `OperatorFiveStepTests.cs`: both per-node and generation execution through real
  production Host/handlers and published native CLI; no effects on acceptance;
  exact generated material/output, owner/ACL/login attributes and real migrations;
  repeats/restart with retained snapshot/bytes/history; missing mandatory handler
  or output support before any effect; real postcommit uncertainty; partial 1/5
  pre-dispatch stop and later explicit safe resume using the original material.
- `OperatorGenerationTests.cs`: real Host/generated RPC exact validation,
  definitive conflict, generation/generation and generation/operation concurrency,
  canceled gate waiter, observable progress during dispatch, cancellation after
  real CREATE followed by restart/read-only inspection and no replay.
- `OperatorExecutionIntegrationTests.cs`: standalone published native generation
  execution/repeat/restart; generation RPC cancellation on both sides of the
  dispatch marker. Existing standalone/pair recovery tests remain mandatory.
- `DurableOperationGraphIntegrationTests.cs`: the same real runner/journal with
  non-database exact handlers proves failure/uncertainty stops independent ready
  work, no replay, safe resume, dependency order and shared serialization. This
  is generic boundary evidence, not a second production Provider claim.

Run solution restore/build/test/format and `git diff --check`; publish with
`PublishAot=true` and `TreatWarningsAsErrors=true`; set `HABY_AOT_CLI_REQUIRED=1`
and `HABY_AOT_CLI_PATH` to that executable before running the complete disposable
PostgreSQL/WSLc suite. CI additionally publishes and runs Linux Native AOT.
Never substitute a fake runner or fixture-only pair admission for production proof.
