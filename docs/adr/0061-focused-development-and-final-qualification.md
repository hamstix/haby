# ADR-061: Focused development and exact-SHA final qualification

- Status: Accepted
- Date: 2026-11-18
- Implementation: Not implemented by this documentation; [issue #263](https://github.com/hamstix/haby/issues/263)
- Order: Finish #259, implement #263, then Kubernetes Provider TLS #261

## Context and amendment

Automatically repeating full PostgreSQL, Native AOT and Kubernetes campaigns for
each diagnostic push delays focused feedback. Fast/helper tests alone also cannot
prove real controller ordering, database constraints or teardown. A hosted-only
choice does not establish that a local Windows integration venue is unavailable.

This decision amends ADR-056 and the every-change wording in the suite-routing
guide: complete applicable qualification remains mandatory before merge, but
ordinary development pushes need not repeat every expensive suite. Until #263 is
implemented with an approved safe gate transition, current workflows and required
checks remain authoritative. This document changes no workflow or remote setting.

## Decisions

### 1. Ordinary iteration

Ordinary code PR pushes run build, fast tests, formatting and change validation.
They do not automatically launch full PostgreSQL, AOT and Kubernetes campaigns for
every diagnostic commit. Preserve the existing lightweight documentation-only
route and its truthful absence of runtime-suite credit.

### 2. Independent focused real scenarios

Expose explicit PostgreSQL testcase/group selection and individual Kubernetes
contour selection without unrelated jobs or suites. Reuse current supported
entrypoints with consistent local/hosted semantics. Windows development uses a
supported WSL/Docker or existing Linux integration venue; hosted focused execution
is a valid fallback, without requiring another environment to be constructed.
Report unavailable prerequisites separately from code failure or a hosted-only
choice. This is not a multi-backend test-platform project.

Selection must execute the requested scope and distinguish zero discovery,
invalid selection, execution failure and cancellation. Partial results say
focused and never count as complete final qualification. A changed orchestration
needs an actual relevant PostgreSQL or Kubernetes scenario, not just helper tests.

### 3. Explicit final qualification

After focused behavior passes and substantive review findings are addressed, the
coordinator explicitly launches final qualification on an immutable exact candidate
SHA. Run all existing applicable gates, including both PostgreSQL suites, both
published Native AOT targets and required Kubernetes contours. Account explicitly
for candidate HEAD, tested merge tree and base according to each gate's contract.
Any newer commit invalidates final evidence; no transfer of green credit.

### 4. Mechanical merge boundary

Successful final qualification on the current candidate must mechanically gate
merge. Draft state, labels, skipped jobs and a chat promise are insufficient.
Focused results cannot satisfy the full qualification gate. Failed, canceled,
missing or stale results, missing artifacts and incomplete coverage fail closed.
Documentation-only lightweight qualification remains an explicit supported case.

Before implementation, specify the exact trusted trigger, permissions and
untrusted-PR boundary, immutable ref/expected-SHA checks, stale-run suppression,
applicable required contexts and branch-protection integration. Never execute an
untrusted ref as trusted workflow control or forge a success status. If contexts
or remote protection settings must change, propose the exact configuration for
maintainer approval before mutation. Stage the transition without an unprotected
window. This ADR grants no remote setting or bypass authority. Architect exact-SHA
merge approval and normal fresh merge checks remain required.

### 5. Complete first-failure evidence

Reuse bounded safe reporting for the complete first failure: stage, outcome,
member and deadline facts when available; primary failure separate from cleanup.
No secret-bearing output or new reporting framework. Do not repeat a full campaign
merely to add one missing diagnostic line. Preserve uncertainty when original
evidence cannot establish historical cause.

### 6. Autonomous focused repair

An agent may analyze, reproduce, narrowly fix and verify an in-scope failure
autonomously through focused checks. Escalate contract, security or scope changes
and concrete unresolved boundaries, rather than each diagnostic line. Blind
unchanged reruns remain prohibited; report lack of progress. This policy does not
retroactively release an already stopped task or authorize remote mutations.

### 7. Preserve tests and limit restructuring

Do not delete tests, weaken coverage, change timeouts, rewrite product behavior or
fixtures, add images/services or introduce mass load for this routing slice.
Later measured hotspots may move pure combinatorics to fast tests; real SQL
constraints, transactions and races remain PostgreSQL obligations. A wholesale
suite migration is not a prerequisite for functional work.

### 8. Validation and success criterion

Test routing, selection, stale SHA/base handling, missing/failed/skipped gates,
permissions and the protection-context transition. Demonstrate one representative
ordinary diagnostic push avoiding automatic full PostgreSQL and independently
reachable real focused PostgreSQL and Kubernetes scenarios. Then run complete
existing applicable qualification on the final implementation candidate before
merge. Do not expand this into a Cartesian workflow campaign.

Compare focused feedback and full qualification separately. No promised speedup
percentage or new benchmark campaign is required. Provide operator and agent
commands, selection/result semantics and limitations. Inspect actual workflow
results independently of author claims, without duplicate unrelated artifact audits.

## Remaining boundaries

Issue #263 follows #259 and precedes #261, amending ADR-060's immediate-after-259
implementation order. Afterwards retain successive UPDATE qualification, ADR-052
correction, integrated rehearsal and public operational UPDATE. ADR-059 CLI
composition remains after operational UPDATE. No P3 completion or public UPDATE
admission is declared here.
