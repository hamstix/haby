# Safe measured-test failure evidence

Issue #188 adds `failure-evidence.json` to the existing measured PostgreSQL
report. It classifies future failures conservatively; it does not recover the
cause of historical #186/#187 failures or change runtime behavior.

## Closed version 1 contract

The [JSON schema](schemas/postgresql-failure-evidence-v1.schema.json) forbids
additional fields at every object level. Consumers must validate the version and
join each record's `method` and `case` to a **Failed** entry in the same run's
`cases.json`. The case hash is unchanged: SHA-256 of the existing qualified
method, newline and original test name. No diagnostic message is hashed.
`measurement.json` remains the source/assembly/run attribution for the directory.
Every definition must have explicit nonempty `className` and `name` attributes;
incomplete identities reject the report through the fixed safe-error path,
including removal of stale exporter-owned files. Normal method/case IDs are
unchanged. The XML element's own name is never a substitute for a missing method
name attribute.

The envelope contains `schemaVersion`, `failedCases`, `emittedRecords`,
`omittedRecords`, `truncated`, the four limit fields and `records` (always an
array, including no failures). `failedCases = emittedRecords + omittedRecords`;
`truncated` is true exactly when omissions are nonzero. Records are ordered by
the existing ordinally stable method/case inventory order within the execution
venue. The sidecar is deterministic for identical input on a venue.

Each record contains only the existing method/case IDs and these closed codes:

| Field | Allowed values |
| --- | --- |
| `classificationMode` | `parsed-signature`, `unavailable` |
| `category` | `unknown`, `assertion`, `timeout`, `cancellation`, `transport` |
| `assertionKind` | `unknown`, `equal`, `true`, `false`, `null`, `not-null` |
| `exceptionCategory` | `unknown`, `timeout`, `cancellation`, `socket`, `grpc` |
| `grpcCode` | `unavailable`, `deadline-exceeded` |
| `socketCode` | `unavailable`, `connection-refused` |
| `phase` | `unavailable` |
| `rootCause` | `unknown` |

`unavailable` in `grpcCode` is also the normalized Unavailable signature code;
`classificationMode` and `exceptionCategory` distinguish a recognized gRPC
signature from missing evidence. These labels describe **text matches only**.
TRX ErrorInfo supplies no authoritative structured exception metadata. No label
confirms a runtime exception, transport event, root cause or phase. A deliberately
forged whole recognized message can receive the same parsed label. Never use it
as runtime proof, an automatic remediation trigger or a substitute for private
diagnosis. Phase is never inferred from method names or stacks.

## Deliberately narrow signatures

Only one ErrorInfo with one non-nested Message text/CDATA node is considered.
The entire message must equal one of the following case-sensitive constants;
there is no whitespace normalization, regex capture, substring search, stack
parsing or extraction from expected/actual, stdout or attachments.

| Exact whole Message | Category; assertion; exception; optional code |
| --- | --- |
| `Assert.Equal() Failure` | assertion; equal; unknown |
| `Assert.True() Failure` | assertion; true; unknown |
| `Assert.False() Failure` | assertion; false; unknown |
| `Assert.Null() Failure` | assertion; null; unknown |
| `Assert.NotNull() Failure` | assertion; not-null; unknown |
| `System.TimeoutException : The operation has timed out.` | timeout; unknown; timeout |
| `System.OperationCanceledException : The operation was canceled.` | cancellation; unknown; cancellation |
| `System.Net.Sockets.SocketException (111): Connection refused` | transport; unknown; socket; connection-refused |
| `System.Net.Sockets.SocketException (10061): No connection could be made because the target machine actively refused it.` | transport; unknown; socket; connection-refused |
| `Grpc.Core.RpcException : Status(StatusCode="Unavailable", Detail="Service is unavailable.")` | transport; unknown; grpc; unavailable |
| `Grpc.Core.RpcException : Status(StatusCode="DeadlineExceeded", Detail="Deadline exceeded.")` | timeout; unknown; grpc; deadline-exceeded |

All other text, missing/duplicate/nested fields, localized or multiline messages,
conflicting signatures and overlong messages produce the all-unknown/unavailable
record. Ordinary assertion messages with expected/actual details will therefore
often remain unknown. This intentional loss of classification prevents protected
payload text from being interpreted as stronger evidence. Future signature
expansion requires explicit review and adversarial fixtures.

## Bounds and publication

The existing XML document limit remains 67,108,864 characters; DTDs and external
entities are prohibited. The sidecar is at most 1,048,576 UTF-8 bytes, with at
most 1,024 records. Diagnostic method IDs are limited to 512 characters and
messages considered for matching to 4,096 characters. Every diagnostic value
other than existing IDs is a fixed constant. Oversize methods and records beyond
the first 1,024 eligible failures are omitted with explicit counts. Canonical
`cases.json`, counts, timings and outcomes retain **all** cases, including those
omitted from this sidecar. An unexpected byte-bound violation rejects the report.

The exporter constructs fresh objects and stages all its output before publishing
the sidecar last. Validation/export failure removes its six owned report files
from the destination, including stale sidecars, and emits only a fixed error.
Other existing measurement files are preserved. This is fail-safe publication,
not a filesystem transaction against process termination: consumers read only a
finished measurement with a successful artifact-export phase. Raw TRX/logs remain
private; the minimal public TRX has no Output, ErrorInfo, Message, StackTrace or
attachments. Export failure preserves an original nonzero runner exit; a failed
build or missing TRX never fabricates zero-test counts.

For example, the harmless synthetic whole message `Assert.Equal() Failure`
produces `category=assertion`, `assertionKind=equal`,
`classificationMode=parsed-signature`, `rootCause=unknown`, `phase=unavailable`.
Appending `Expected: synthetic-value` makes the classification unavailable.

## Artifact and consumer map

| Artifact/consumer | Change |
| --- | --- |
| `cases.json`, `counts.json`, duration CSVs | Existing identity/outcome/timing shapes unchanged |
| `results.trx` | Existing minimal reconstructed shape, no diagnostics added |
| `measurement.json`, `setup.csv` | Existing run/phase and test setup timing semantics unchanged |
| `failure-evidence.json` | New additive version 1 sidecar |
| `compare-postgresql-timing.ps1` | Still reads only canonical cases; no sidecar dependency |
| Build workflow upload | Explicit eight-file allowlist including the new sidecar |
| Local evidence collectors | Add this exact sidecar name; never upload raw logs/TRX or directory-wide extras |

Run `scripts/test-postgresql-timing-export.ps1` on actual Windows and Linux
PowerShell first. It invokes the synthetic closed-schema/privacy/cap fixtures
and exercises the real exporter/orchestrator with a deliberately failing fake
runner, separately from real solution/integration/native gates. Follow the full
[local validation protocol](local-validation.md); synthetic success is not a
successful real integration run. Keep exact source, original failures, omitted
gates and private evidence provenance in task-owned evidence.
