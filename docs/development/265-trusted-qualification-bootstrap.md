# Trusted final qualification control bootstrap

This is the first stage of [#263](https://github.com/hamstix/haby/issues/263),
tracked separately by [#265](https://github.com/hamstix/haby/issues/265) under
[ADR-061](../adr/0061-focused-development-and-final-qualification.md).
It installs control for a successor candidate. It does not reduce ordinary PR
checks, change protection settings, complete #263 or authorize merge.

## Installation before use

GitHub requires a manually dispatched workflow to exist on the default branch.
The bootstrap PR therefore runs the existing automatic full Build, PostgreSQL,
both Native AOT targets and applicable Kubernetes checks, with exact-SHA review
and architect approval before normal squash merge. Its candidate workflow cannot
certify itself as trusted main control. Only after installation does a successor
candidate exercise the installed dispatcher. Keep #263 open through that exercise
and the separately approved routing/protection transition.

The existing workflow files gain a read-only reusable entrypoint. Their push/PR
events, full-job conditions, fixed check names, commands, coverage assertions,
images, timeouts and documentation-only route remain. An explicit reusable Build
invocation requests all existing runtime gates, including for documentation; the
ordinary docs-only PR route stays lightweight. Lightweight final documentation
qualification belongs to the subsequent #263 transition.

## Exact trigger and trust

Dispatch `.github/workflows/final-qualification.yml` at **main**, supplying the PR
number and all four full immutable identities: expected HEAD, main base, tested
merge commit and merge tree. Resolve these from the current PR and its merge
commit before dispatch. Branch names, abbreviated SHAs, labels and draft state
are not qualification identities. Dispatch requires the actor's current
write/maintain/admin repository permission.

The control verifies its workflow ref is the installed main workflow and its
immutable workflow SHA equals the expected current main base. It queries the PR,
checks open state, repository/base, HEAD and merge SHA, then verifies the merge
commit's ordered parents and tree. Missing, changing or unavailable merge data
fails closed. Concurrent dispatches are serialized per PR and newer dispatches
cancel older ones; a canceled run cannot qualify.

Every freshness check reads the current `main` branch SHA and verifies it equals
the immutable expected base. The PR's `base.sha` can retain an older snapshot
after main advances; it is not the current-base authority. Keep the PR's base ref
and repository checks, and bind the actual tested merge's first parent to current
main, its second parent to expected HEAD and its tree to the expected tested tree.
Missing or changed live main fails closed, including after control publication.

Reusable workflow definitions come from the installed control commit. Candidate
jobs check out only the verified tested merge commit, with read-only permissions,
no inherited secrets and no persisted checkout credentials. They execute candidate
code on disposable hosted runners. The separate start/publish jobs hold
`checks: write`; the publisher also has `actions: read`. Neither executes candidate
code, scripts or artifacts. Its module comes from the immutable installed workflow
SHA. Inputs enter structured API fields/environment values, never shell commands.

**Pilot maintainer trust is explicit.** Maintainers with workflow-edit or admin
rights are trusted, and architect review covers workflow changes. Yes: code in a
same-repository candidate PR workflow that can obtain a write-capable token can
create a check named `Final qualification` from the GitHub Actions app **before
maintainer approval**. Requiring a status name plus the Actions app cannot identify
this control workflow. The bootstrap does not prevent that and must not be
represented as isolation from all same-repository workflow writers. Fork candidate
code executed by this control receives no check-write token or repository secrets.
No separate issuer/App/service is installed. A stronger writer threat model would
require a separately approved trusted issuer or enforced workflow policy.

## Actual full result, coverage and cleanup

The start job records `Final qualification` in progress on the exact PR HEAD.
After the actual jobs finish, the trusted publisher checks both reusable outcomes
and the eight individual required job outcomes from the current run attempt. Any
failure, cancellation, skip, missing job or ambiguous result prevents success.
The normal PR Dependency Review remains a separate ordinary check; it is not
claimed as executed by a manual event that lacks a PR payload.

The publisher requires this invocation's nonexpired artifacts and each mandatory
member; `if-no-files-found: error` alone is insufficient. It compares existing
case identities and theory multiplicity against the tested candidate's existing
suite/gate map, with only passed rows and complete counts. It checks:

- Fast inventory and both published Native AOT inventories, native/consumer hashes
  and compiler source/RID evidence.
- Both full PostgreSQL suites, complete coverage, unchanged source, required AOT,
  named reflection precheck, primary outcome and per-suite owned resource cleanup.
  The published composition executable must match the actually tested hash.
- Provider API success and existing intentional failure/teardown rehearsals;
  kernel, durable and configured lifecycle inventories and their real owned
  catalog/namespace/container/storage/private-directory cleanup markers.

Focused, selected-only, stale, incomplete or missing evidence cannot count as full
qualification. This reuses existing safe reports and coverage mapping, not a new
per-byte evidence ledger. Candidate artifacts are read as bounded data and never
executed or printed. Parser/API errors do not echo their contents.

Immediately before completing the real aggregate check, the publisher fetches
live PR/merge identities again. A newer HEAD has no matching check; a changed
base/tree invalidates the result. A compact control artifact records identities,
run/attempt, reduction outcome and per-gate counts/cleanup. Reduction first stages
this report, then the required control upload returns an artifact ID. The final
step verifies that artifact's name, ID, current run, availability and nonempty
size before rechecking live identities and completing the candidate check. Failed,
canceled, skipped or missing upload cannot grant success. The immutable artifact
records the prepublication reduction; the check records the final publication and
freshness result. Cancellation or missing publisher
leaves an incomplete check rather than a success. Architect merge approval remains
separate from successful execution.

Failure reports retain a bounded primary code, stage, expected gate/member and
outcome, with deadline facts explicitly unknown when unavailable and cleanup as a
separate state. Missing members, incomplete cleanup and stale candidates remain
distinct; unknown parser/API failures use a fixed code and never echo their raw
messages. The check retains the first reduction failure when publication follows.

The PostgreSQL artifact uses archive-relative `cases.json`, `measurement.json`,
`functional/cases.json`, etc. Its producer uploads only paths beneath `safe/`, so
that directory is the archive root. Native artifacts also upload `compiler.json`
beside `safe/`, retaining their `safe/` prefix. A focused contract test checks the
PostgreSQL fixture against the actual producer path list. See the upload action's
[multiple-path root and artifact ID contract](https://github.com/actions/upload-artifact/blob/v4/README.md).

## Minimal protection proposal, not applied

Public reads during preparation showed active all-branches ruleset `27124428`
containing creation/deletion restrictions only, and disabled main ruleset
`27124350`. The unauthenticated legacy protection endpoint returned 401. Legacy
protection and its current required contexts are **unknown**, not absent. Obtain
their current values through a maintainer-owned authenticated channel; do not
work around the 401 or infer them from the public ruleset list.

After installation and the successor's demonstrated positive and negative gate
behavior, the maintainer must review and explicitly approve this minimal delta:

1. Preserve all existing protections/review requirements. Add the real
   `Final qualification` context from its actual GitHub Actions issuer and enable
   strict/up-to-date base checking, while keeping every existing required context.
   Verify the maintainer trust limitation above before approval.
2. Verify missing, stale and partial qualification prevents merge and current full
   qualification is recognized. Do not use bypass to demonstrate eligibility.
3. Only then approve replacement of legacy expensive required contexts, if present,
   with the full aggregate while retaining `validate`, `dependency-review` and
   existing review, deletion and force-push protections. Activate ordinary fast
   routing in the successor change with no unprotected interval.

Do not enable the disabled main ruleset wholesale: it contains unrelated update
restrictions. No remote mutation is authorized by either issue. A name-only check
does not establish workflow provenance. Strict up-to-date protection is necessary
to prevent a prior green from surviving a later base advance after publication.

## Commands and evidence limits

Run the focused control tests locally without infrastructure:

```powershell
node --test ./scripts/test-final-qualification.mjs ./scripts/test-ci-change-routing.mjs
```

After installation, a maintainer/coordinator can use the Actions **Run workflow**
form on main with `pr`, `head`, `base`, `merge`, `tree`, or an approved structured
workflow-dispatch API call with those same inputs and `ref: main`. The control
deliberately refuses dispatch from a topic branch. Never dispatch the bootstrap
candidate as though its control were installed. No bootstrap run is presented as
proof of the postinstallation path; the successor provides that real evidence.

Do not blindly rerun unchanged failures. Preserve the first primary stage/member,
deadline and cleanup evidence. A new required hosted failure stops further cycles
pending the coordinator's bounded disposition. Parent #263 retains focused real
PostgreSQL/Kubernetes selection, routing demonstration and final qualification
obligations; this installation does not transfer passing credit to it.
