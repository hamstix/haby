# Local validation when hosted runners are unavailable

This is a historical quota fallback, not the current maintenance default. Use the
[hosted suite/gate workflow](197-suite-routing.md) under ADR-056. Do not resume
#188/#191 or this old run chain without the architect's explicit reassessment.
Any separately authorized fast source inspection now requires the explicit
`HABY_TEST_SOURCE_ROOT` described in that guide; no build-output ancestry is used.

This temporary execution-venue protocol records the
[approved local equivalent](https://github.com/hamstix/haby/issues/183#issuecomment-6807444676)
for sequential P3 slices while hosted Actions quota is exhausted. It changes no
functional, security, review or branch-protection requirement. Stop using it when
the user supplies runners or supersedes the protocol. Do not launch or wait for
additional hosted runs under this protocol.

## Freeze source and own the venue

Record full HEAD, base, tree, UTC timestamps, Git status and tracked-diff SHA256
before and after every gate. Use one clean exact-commit checkout per OS venue,
with separate outputs/bin/obj. A verified local Git bundle/clone may transport
the commit. Do not share a mutable checkout with another execution. A new commit
requires new evidence; historical PASS results cannot be relabeled.

Record actual OS/architecture, SDK/runtime and tools, exact non-secret commands
and configuration. Follow `global.json`, including its allowed patched SDK
selection, and audit the actual native compiler packages selected by that SDK.
Windows must execute win-x64 and Linux must execute linux-x64. Cross-publication
alone proves neither. Keep first-use certificate generation disabled in isolated
Linux tools; do not change machine-wide trust, firewall, networking or services.

Use only positively owned ephemeral PostgreSQL/Host/CLI resources. No ambient
kubeconfig, customer catalog, persistent developer database or secret-bearing host
mount is a default input. Check actual Docker compatibility before choosing
Testcontainers: ordinary WSL2 and WSLc do not imply a Docker daemon. See the
[PostgreSQL runtime guide](postgresql-integration-tests.md) and the existing
`scripts/test-postgresql-wslc.ps1` helper. External mode needs explicit opt-in,
an invocation-owned container and ephemeral connection supplied privately.

Run heavy pools and full gates sequentially. Ephemeral prerequisites may be
installed inside a disposable container. Cleanup must verify ownership and remove
only the exact recorded processes/containers/volumes, with cleanup failures recorded
separately. Never use broad prune or speculative cleanup of another task's resources.

### Optional transparent loopback route

Some Linux-on-Windows venues require a guest loopback route to the owned WSLc
PostgreSQL endpoint because existing tests intentionally switch `127.0.0.1` and
`localhost`. Verify that both names reach the same owned server before the full
run. A task-local byte relay is permitted only when its upstream endpoint and
actual container port binding are verified, and its process/run identity and
lifetime are controlled. Bind explicit IPv4/IPv6 loopback addresses only.

It must preserve one frontend/one upstream connection, bytes, authentication and
TLS behavior: no pooling, SQL inspection, TLS termination, retries or altered
test timeouts. Prove actual authentication/query/server identity on both names,
wrong-password rejection and the unchanged pinned-endpoint test first. Preserve
probe/lifecycle/failure evidence. Check the runtime's real inspect schema rather
than assuming Docker-style field paths. A relay cannot turn connection failures
into success or justify a production/test-source workaround. Never assume an old
task's helpers or output paths exist; any wrapper belongs in fresh ignored task
outputs and must be reviewed/verified for the current source and venue.

## Execute every applicable gate

1. Run CONTRIBUTING solution restore/build/test/format/diff. Record actual errors,
   warnings, counts and skips. Run focused tests first. Keep optional integration
   format failures with a verified baseline distinction; do not silently omit them.
2. On actual Linux, run the unchanged `scripts/test-postgresql-measured.ps1` over
   the complete PostgreSQL suite with `HABY_AOT_CLI_REQUIRED=1` and the published
   linux-x64 CLI selected by `HABY_AOT_CLI_PATH`. No filters, skips, timeout/pool
   inflation or removed cases. Retain exact case inventory, source-change flag,
   required-native flag, restore/build/test/export phases and exits. Compare the
   prior complete inventory and explain additions or missing cases.
3. On both actual OSes, follow `.github/workflows/build.yml`: warnings-as-errors
   Native AOT publication, published help/version, and the entire published-binary
   loopback selection in required-native mode. On Linux run
   `scripts/ensure-native-aot-prerequisites.sh`; pass its verified compiler and bfd
   linker explicitly. Do not replace these gates with JIT or cross-published runs.
4. Run `scripts/test-postgresql-timing-export.ps1` and
   `scripts/test-native-aot-prerequisites.sh` for safety/redaction/failure-propagation
   and fallback controls. Keep real-tool Linux prerequisite proof separately from
   its fake-tool controls. Use the existing sanitized exporter unchanged.
5. Run a current complete transitive vulnerability audit and record restore/audit
   sources, tool status and full closure. Follow repository policies, including
   any stricter no-advisory threshold in the workflow. Source/network failure is
   UNKNOWN or FAILED, never zero advisories. Do not suppress alerts or reduce
   severity thresholds to obtain a green result.
6. Compare base/head dependency manifests/locks and actual resolved closure for
   licensing/dependency changes. Verified no-delta plus a current full audit is
   the permitted local Dependency Review equivalent. If dependencies changed,
   inspect exact binaries, transitive terms and advisories under repository policy;
   unresolved or restrictive terms block. Never claim the hosted service ran.
7. Add real runtime/Kubernetes correctness proof when the slice implements that behavior.
   The separate >=150-client/two-document load rehearsal is deferred until after P4
   under [ADR-055 section 17](../adr/0055-p3-implementation-closure-and-coordination.md#17-load-proof-and-test-cadence);
   it does not gate P3 completion or current runtime-delivery acceptance. Ordinary
   correctness/security checks remain required.
   Metadata/input-set work adds no cluster/load gate. Existing qualification carries
   only while its qualified code and dependencies remain unchanged.

After a failed full run, diagnose concrete failed cases and classify the evidence
before deciding on another run. Preserve the original failure, source, commands,
raw provenance and any diagnostic-only results. No blind full reruns, blanket
environment classification, source changes to hide failures, or success summaries
that discard failed/NOT RUN gates.

## Evidence, review and merge

Store per-issue/full-SHA results outside tracked product source. Keep raw logs,
TRX, credentials, dumps and connection strings private. Publish only an explicit
safe allowlist through the existing exporter. A gate table records exact source/
tree, tools, commands, UTC bounds, outcomes/counts, evidence paths and SHA256, plus
all failures, omissions and replaced hosted checks marked NOT RUN.

An independent reviewer reads original safe evidence, hashes and provenance,
checks policy equivalence and reports actionable findings externally. Architecture
acceptance is exact-SHA; another commit invalidates it. Local technical acceptance
does not fabricate hosted check status or satisfy required GitHub contexts by itself.

Inspect actual protection immediately before any authorized merge. Never disable,
bypass or forge protections/statuses. If required hosted checks alone block normal
merge, retain `BLOCKED_FOR_MERGE` until runners or an explicit separately reviewed
user policy decision resolves it. This protocol grants no merge authorization.
