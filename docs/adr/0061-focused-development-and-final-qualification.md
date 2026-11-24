# ADR-061: Focused development, nightly regression and release qualification

- Status: Accepted; amended 2026-11-24
- Original date: 2026-11-18
- Implementation: Policy acceptance is not workflow deployment. [Issue #297](https://github.com/hamstix/haby/issues/297) implements this amendment separately from frozen issue #263.
- Supersedes: The original full-qualification-before-every-merge requirement in this ADR and conflicting merge-gate wording in ADR-056 and development guides, after the safe mechanical transition below.

## Context

Repeated complete PostgreSQL and installed qualification campaigns delay feedback
on narrow changes. A development merge and a release are different decisions.
During active development, main may contain a regression detected by nightly
qualification; merging does not declare that revision ready for installation.
The maintainer explicitly accepts this tradeoff. Main must remain buildable, and
known blocking defects must not be hidden or described as qualified behavior.

The original 2026-11-18 policy required complete exact-candidate qualification
before every merge. The accepted 2026-11-24 amendment replaces that requirement
with targeted real tests and smoke before merge, full nightly regression and full
qualification before release. Existing running workflows and repository rules
remain effective until the safe transition is implemented; this ADR grants no
bypass, forged success, retroactive approval or permission to ignore a failed run.

## Decisions

### 1. Development feedback

Run build, fast unit/contract tests, formatting and change validation during
ordinary development. Run focused tests for the changed behavior before broad
campaigns. Preserve the lightweight docs-only route. A repair should first prove
the relevant regression and adjacent behavior, not repeat the full suite merely
to discover whether a small correction worked.

Use deterministic fakes at existing application interfaces for pure rules and
orchestration. Do not mass-convert persistence tests to EF InMemory or SQLite.
PostgreSQL transactions, constraints, query behavior, migrations, locks and races
remain real-PostgreSQL obligations. Reorganizing the entire suite is not a
prerequisite to changing scheduling and gates.

### 2. Pre-merge qualification

Require fast checks, independent source review, the real integration scenarios
relevant to the change and a small fixed PostgreSQL smoke set. Bind results to
the exact candidate and tested base/tree. PostgreSQL-related changes cannot pass
on helper tests alone. Relevant public RPC, security, AOT or Kubernetes behavior
still requires appropriate focused evidence; this is not a waiver for other
changed integrations.

Choose scope from changed behavior and dependencies, not branch names or the
author's preferred tests. Changes to migrations, transaction/locking semantics,
secret authority or shared persistence require a broader relevant PostgreSQL set.
Document the selection and review it independently. Unknown impact requires
explicit broader selection; empty discovery, invalid filters, failed/missing
results or stale evidence never count as success. No universal promise that every
change can merge after the smallest selection.

Keep the smoke set small and representative of catalog initialization and one
ordinary durable public path. Version its concrete membership in the implementation
and use existing test identities and execution venues. Do not gradually duplicate
the entire regression suite under the name smoke.

### 3. Full nightly regression

Run the complete PostgreSQL regression, including existing functional and
infrastructure coverage, once nightly on a captured immutable main SHA. Preserve
all tests and honest case accounting. Use the existing full systemic qualification
entrypoints on the documented periodic schedule; do not create a second harness.
Nightly execution is not a prerequisite that every intervening PR must await.
A queued run identifies its tested SHA even if main advances while it executes.

Publish a concise result with tested SHA, failures, safe diagnostics and cleanup
status. Missing, canceled or incomplete nightly runs are not green. Preserve
first-failure evidence and distinguish code failure from venue failure. A failed
nightly is a prioritized repair input, not permission for repeated unchanged runs
until green. No retroactive green credit for older failed candidates.

### 4. Full qualification before release or installation

Before releasing or updating a real installation, run complete applicable
qualification on the exact candidate: PostgreSQL suites, published Native AOT
targets and required Kubernetes/system contours. Check candidate/base/tree and
artifact identity according to each existing gate's contract. A nightly result
may supply evidence only if it satisfies that exact candidate and complete scope;
a green run on an ancestor is insufficient. A changed candidate requires new
applicable release evidence. A pre-merge targeted pass is never labeled full
release qualification.

### 5. Responding to failures

Triage nightly failures in the next development cycle and prioritize focused
reproduction and correction. Confirmed data-integrity or authorization regressions
block affected changes and release/installation. Unrelated work is not automatically
frozen by every nightly failure. Record disposition and affected scope explicitly;
unknown failures stay unknown until evidence supports a cause. Release qualification
cannot ignore an applicable unresolved failure.

Do not delete or quarantine tests, relax assertions, increase budgets or replace
real database semantics merely to obtain green status. A justified test correction
remains a reviewed code change with focused evidence. An existing mandatory failed
candidate is not automatically approved by this policy amendment.

### 6. Safe mechanical gate transition

Implement distinct truthful merge and release qualification outcomes. Reuse the
current routing, selectors, artifact reporting and workflow control. Do not mark
an unexecuted full suite successful or reuse its full-qualified status to mean
smoke-only. Specify required check names, trusted workflow/ref boundaries, stale
SHA/base handling, permissions and failure behavior before switching protection.
Keep a mechanically enforced gate throughout the transition.

If remote required checks must change, present the exact proposed settings for
maintainer approval before mutation. This policy does not authorize changing
repository settings or bypassing branch protection. Architect approval remains
exact-SHA and independent from coordinator merge. After the new route is deployed,
its targeted merge requirements replace the former full-merge requirements;
full release checks remain separate. Update CONTRIBUTING, routing/agent guides and
coordinator instructions so they do not retain contradictory mandatory full-merge
rules. No permanent parallel legacy qualification scheme is required.

### 7. Bounded implementation and evidence

Deliver scheduling/selection/gate changes without a broad fixture rewrite or new
test platform. Demonstrate one representative targeted PR, nightly full execution
on pinned main and release qualification routing. Reuse already valid exact-source
artifacts where appropriate; do not run a Cartesian campaign of every workflow
combination. Test selectors, empty/invalid selection, sensitive-change expansion,
stale/failed/missing checks and safe report publication with cheap controls.

Report targeted feedback and full runtime separately, without promised speedup
percentages. Keep original safe failure observations and cleanup outcomes; obtain
artifacts once and avoid duplicate coordinator/reviewer audits. Existing local
Windows/WSL and Linux venues remain usable. Broad test optimization is not a new
blocker for functional delivery.

## Scope and sequencing

This amendment concerns validation policy, not product capability. It adds no
Provider, Workload lifecycle, CLI or compatibility requirement. Coordinate its
bounded implementation with active work; preserve the next functional priority
for pre-Commit import correction and do not revive the frozen #263 campaign as a
prerequisite. Docs acceptance alone declares neither P3 completion nor a release.
