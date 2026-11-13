# Bounded failure evidence

The current timing exporter adds `failure-evidence.json` beside its canonical
case/count/minimal-TRX files. The same code exports each PostgreSQL suite, their
aggregate, fast tests and published-native gates. Build and weekly qualification
publish the sidecar through explicit file allowlists. Existing case/parameter
ledgers, suite selection, resource accounting and test-exit precedence remain.

## Identity and trust

The closed [v1 schema](schemas/postgresql-failure-evidence-v1.schema.json) joins a
record to exactly one **failed row** using `method`, `case` and `row`. Equal
display hashes can belong to different actual theory executions: their `row`
identities and diagnostic associations remain distinct across TRX inputs.
Duplicate actual executions are rejected, not deduplicated. Passed/skipped rows
receive no failure record. Successful runs have an explicit empty sidecar.

`classificationMode: parsed-signature` means an untrusted message exactly matched
one small curated English signature. It is not a runtime exception type, verified
assertion, gRPC/socket observation or root cause. Even a forged exact signature
can receive this label; it proves only text equality. Known values cover five
assertion signatures, timeout/cancellation, two connection-refused messages and
two gRPC statuses. No captures or raw text are emitted.

Missing, conflicting, nested, localized, multiline, secret-bearing or otherwise
unrecognized messages conservatively remain `unknown`/`unavailable`. Only one
plain-text/CDATA Message in one ErrorInfo is considered. Stacks, stdout, expected/
actual values, attachments and arbitrary XML attributes are never diagnostic
sources. `phase` is always `unavailable`; `rootCause` is always `unknown`.
Existing structured Host CSV instrumentation remains separate and unchanged.

## Bounds and publication

- At most **1024 diagnostic records** and **1 MiB of UTF-8 sidecar bytes**.
- A method longer than **512 characters** is omitted only from diagnostics.
- At most **4096 characters** of a message are inspected. Longer messages remain
  unknown; text is never truncated into a recognized signature.
- `failedCases`, `emittedRecords`, `omittedRecords` and `truncated` explicitly
  account for record/method/byte limits. Canonical identities and rows are never
  truncated or dropped to satisfy diagnostic caps.
- Diagnostic candidates are bounded to 1024 during parsing and selected in ordinal
  method/case/row order, independent of TRX input order. Byte limiting removes
  trailing diagnostic records only. XML retains its existing 64-Mi-character cap
  per input, prohibited DTDs and disabled external resolution; this is not a new
  streaming parser or a global bound on all canonical inventories.

Definitions require explicit nonempty IDs, class and method attributes, one
TestMethod and no duplicate IDs within an input. Result IDs/display identities
must be present and resolve in that input's definition map. Duplicate/ambiguous
TRX sections and malformed XML are refused with a fixed value-free error.

The exporter stages only its six owned files: cases/counts, two timing CSVs,
minimal TRX and the sidecar. Existing owned reports are invalidated before parsing;
all six are withheld/removed after handled parse or publication failure. The
sidecar is moved last. This is handled-error publication safety, not a crash-proof
multi-file filesystem transaction. Runner measurement/resources, setup/Host CSVs
and unrelated files are never staged or replaced. Raw inputs must lie outside the
output directory; overlapping paths and symlink/reparse aliases are refused before
output mutation. Direct invocation also returns a fixed error, without raw parser,
message or path diagnostics. Existing callers preserve the primary test exit when
export fails. Missing/build-failed/unperformed tests cannot fabricate sidecar rows.

## Validation and historical limits

Run `scripts/test-postgresql-failure-evidence.ps1` in PowerShell 7 on Windows and
Linux. Adversarial fixtures check every public file and captured console, schema,
failed-row joins including repeated displays across inputs, exact/over limits,
determinism, malformed identities/XML, input/output ownership and publication
failures before, during and after the sidecar move. Synthetic serializer whitespace
exercises exactly 1 MiB and one byte over; the ordinary maximum-width closed records
also remain bounded. Existing timing, entrypoint and suite controls remain required.

These controls use synthetic failures; no product failure is induced. Ordinary
solution/format checks and the current hosted full functional/qualification,
Windows/Linux Native AOT and dependency gates remain acceptance requirements.

The anonymous reflection Host test shares a small test-only assertion boundary
with seven deterministic controls. Exact `Unauthenticated` may surface during
request write or response read; wrong statuses, non-RPC errors and missing errors
remain failures. HTTP/loopback checks, the anonymous request, structured phases
and primary-before-cleanup handling remain. This adds seven regular functional
rows (900 PostgreSQL total), not a product authentication or transport change.

Build's existing Linux PostgreSQL job enables `-AnonymousReflectionPrecheck` on
the Docker/full entrypoint. After shared native/preflight/restore/build/discovery,
it executes only the named anonymous-reflection Host case with `--no-build
--no-restore`, before either full suite. Its private logs/TRX and safe
`anonymous-reflection-precheck/` artifacts are isolated. `precheck.json` always
labels this scope `partial`; its row is not included in full-suite aggregation.
Passing requires exactly one matching passed row, zero skips, successful export
and no Host failure/cleanup CSV. Cleanup confirmation means the named case
completed the existing awaited call/channel/Host disposal lifecycle; this
database-free case owns no PostgreSQL fixture. Any test, inventory, cleanup or
reporting failure stops before full execution and preserves the primary exit.
The full gate still executes every mapped row once after a successful precheck;
the extra focused validation never substitutes for that case's full-gate row.
There is no arbitrary filter option or automatic retry. The regular
`test-anonymous-reflection-precheck.ps1` controls prove this stop-before-full flow
with synthetic tools, including missing/malformed output and metadata failure.

This slice selectively adapts the closed signatures/schema and adversarial ideas
from closed, unmerged PR #190 to current main. It does not import its single-TRX,
case-keyed identity logic or the old qualification/index/runtime machinery.
Historical TLS cause remains unknown; old failed evidence is preserved. Original
external/WSLc #191 guarantees remain superseded and unsupported. Functional P3
stays paused until this narrow completion is accepted.
