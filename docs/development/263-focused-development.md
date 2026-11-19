# Focused development after trusted-control installation

Issue263 continues after bootstrap265 and the focused-development successor.
The scope-compatible control was installed by
[PR #272](https://github.com/hamstix/haby/pull/272) at main commit
`ef048c451c3c0d9475b9056b4b544734a070d5b5`. Its implementation candidate passed
full qualification through the previously installed control; that result does
not demonstrate the newly installed docs-only path.

The next evidence step is one ordinary documentation change reviewed at its exact
SHA, followed by one installed-control invocation over its current immutable
base/head/tested-merge/tree. A successful docs-only result must attest documentation
checks, report no runtime execution and grant no runtime case or cleanup credit.
Do not launch a full campaign merely to demonstrate this lightweight path.

Installation does not activate fast-only ordinary code routing or change
protection. Existing automatic routes and their applicable full gates remain.
A routing/protection transition still needs a maintainer-owned authenticated
snapshot, separate review and explicit approval. An unauthenticated401 leaves
protection unknown. Neither installation nor the documentation demonstration
completes the parent issue or authorizes settings changes, bypass or merge.

## Exact PostgreSQL selection

Use one exact fully qualified method or class, never an arbitrary test filter:

```powershell
./scripts/test-postgresql-wslc.ps1 -TestCase Haby.PostgreSql.IntegrationTests.AdministrativeSecretTests.Database_independently_enforces_name_number_and_receipt_uniqueness -ResultsDirectory .artifacts/focused-pg-example
# Existing Linux Docker venue; an exact class includes all its theory rows.
./scripts/test-postgresql-measured.ps1 -Docker -TestGroup Haby.PostgreSql.IntegrationTests.AdministrativeSecretTests -ResultsDirectory .artifacts/focused-pg-group-example
```

The selectors are mutually exclusive. Invalid syntax, an unknown current-plan
selector and incompatible suite/precheck options fail before owned runtime setup.
The current full discovery is reconciled against the unchanged baseline and
ledger. Execution then discovers/runs only the selected exact method union in
its owning suite(s), retaining all selected theory identities and multiplicity.
Zero filtered discovery, discovery mismatch, skipped/missing execution, test
failure, cancellation and reporting/cleanup failure cannot become success.
No unrelated suite or reflection precheck runs in focused mode.

Coverage status/selection are `focused`; measurement qualification is
`focused-only-not-final`. Current source SHA, tracked diff, assembly identity and
source-change detection remain. A dirty-source focused report is not an immutable
final qualification. Existing full and suite paths remain unchanged.

Windows uses the existing WSLc ephemeral external fixture; Linux Docker retains
existing owned resource accounting. External focused execution does not pretend
that Docker resource counters were produced. Fixture SQL cleanup remains a test
obligation. The Windows wrapper additionally records its primary/final exit and
owned stop acknowledgement in measurement.wrapperCleanup; stop failure cannot
turn a test failure into success or leave an otherwise successful wrapper green.
A stop acknowledgement is not a global runtime-inventory proof. Hard termination
of the controlling process can leave cleanup unknown; no successful report is
promised for an abandoned process.

## One Kubernetes contour

The local launcher already exposes separate DeploymentKernel, DurableActions
and ConfiguredDeployment switches; conflicting switches now fail before any
build/runtime/private-directory setup. The accepted269 fixture clocks, normal
Pod grace, ownership guards, all-Pod absence and safe-report limits are unchanged.

The existing Kubernetes Deployment kernel workflow has a manual `contour` choice:
`all` (unchanged default), `deployment-kernel`, `durable-actions` or
`configured-deployment`. A single focused contour requires `expected_head` equal
to the immutable SHA of the selected workflow ref. Unknown/stale input fails
without launching a runtime contour. Focused job names and artifacts have distinct
`Focused` / `focused-only-not-final-` identities. A selected contour retains its
entire current case ledger and owned cleanup assertions; it is partial campaign
evidence and cannot count as full final qualification. Automatic PR and trusted
workflow_call execution still run all three contours with their original names.
Reusable workflows inherit the caller's event context: the installed final caller
is itself a workflow_dispatch. A nonempty required candidate therefore explicitly
selects full execution before direct-dispatch classification. Focused admission,
names and artifact prefixes require an absent candidate. Missing direct inputs
remain invalid rather than being mistaken for a reusable call.

The existing Infrastructure qualification workflow accepts manual `testcase` or
`testgroup` plus `expected_head` for hosted focused PG. It validates current-plan
selection before native publication/runtime setup. With neither selector, its
existing scheduled/manual infrastructure suite remains unchanged. Focused names
and artifact prefixes cannot satisfy the installed final reducer.

Hosted focused execution is a venue choice, not proof that local Kubernetes is
unavailable. Use only existing pinned images and current launchers; no additional
backend, runtime experiment or full campaign is needed for a selector demo.

## Final invocation and subsequent transition

After focused checks and substantive exact-SHA review acceptance, the coordinator
may perform one installed-main final invocation. Resolve all inputs immediately
before dispatch, never copy historical success identities:

```json
{
  "ref": "main",
  "inputs": {
    "pr": "<current successor PR number>",
    "head": "<full current candidate SHA>",
    "base": "<live main and installed workflow-control SHA>",
    "merge": "<live PR tested merge SHA>",
    "tree": "<that merge commit tree SHA>"
  }
}
```

Workflow: `.github/workflows/final-qualification.yml`. Live tested parents must
be exactly `[base, head]`; main must still equal control/base. The actor needs
write/maintain/admin permission. The installed control owns privilege separation,
full-gate reduction, required control publication and final freshness checks.
Selected or renamed jobs/artifacts cannot enter its full evidence contract.
Schedule deliberately around already-running automatic expensive jobs; do not
repeat full dispatches for diagnostics. A new required hosted failure stops new
cycles until bounded disposition; source/base movement invalidates evidence.

Only after the installed-control demonstration and final review may a concrete
routing/protection transition be proposed for separate approval. Retain every
old gate until that approval and verified stronger current-candidate enforcement.
Obtain legacy protection through a maintainer-owned authenticated channel; do not
bypass an unauthenticated401 or enable a disabled ruleset wholesale. Pilot
maintainer workflow-edit trust still applies: Actions app plus a context name is
not workflow provenance. Ordinary docs routing remains lightweight; this phase
neither activates a fast-only code route nor claims a docs-only runtime credit.

## Scope-compatible final control, without routing activation

The installed-control extension derives scope from the complete immutable
base-to-tested-merge change using the existing trusted classifier, whitespace
and local Markdown-link checks. It fetches Git data only; privileged jobs never
check out or execute candidate code. There is no manual docs-mode override or
new privileged trigger. Code/mixed/unknown/empty changes remain full; failed or
missing classification never grants skip authority.

A verified docs-only report has `runtimeExecuted=false`, `cleanup=not-applicable`
and a documentation-check attestation without runtime case counts. Only the
expected skipped build/api/workloads groups are permitted. Missing/canceled
outcomes or unexpected runtime jobs fail closed. Invalid classification cannot
produce a verified scope. Both docs and full paths bind classification to the
same immutable identities and run, require the compact control publication and
perform final live freshness before completing `Final qualification`.

Build now exposes separate `full` validation and `runtime` outputs, but `runtime`
is an exact alias of the existing routing output. Ordinary code therefore still
runs every old expensive gate; ordinary docs remain lightweight, and explicit
candidate calls remain full. No activation flag, remote variable or speculative
disabled infrastructure is introduced. Future fast-only activation requires its
own reviewed source SHA, owning protection snapshot and explicit approval.

The docs control is now installed, so a real docs-only demonstration must use
that installed main revision and a subsequent reviewed documentation candidate.
The earlier implementation qualification cannot provide that demonstration or
qualify a newer source/tree. Local pure Git/control checks do not require
PostgreSQL or Kubernetes runs.
