# ADR-056: Test responsibilities, isolation and execution

Status: Accepted

Date: 2026-11-13

Related: ADR-026, ADR-033, ADR-035, ADR-055

## Context

The investigation behind issues #188 and #191 exposed coupling between catalog
tests, Provider effects, Host/CLI scenarios and test-runtime recovery. Shared
PostgreSQL servers do not isolate cluster-wide roles. Cross-system execution also
introduces process, networking, filesystem and cleanup requirements unrelated to
most product assertions. Build-output ancestry and temporary-file permissions
have become hidden prerequisites. Excessive evidence preparation has further
increased the cost of ordinary validation.

Preserve real integration guarantees while simplifying their execution and
diagnosis. This decision accepts a maintenance stage, not a completed migration
or a claim that current failures are fixed.

## Decision

### 1. Reproducibility before speed

A documented command must produce attributable results and actionable failures
without bespoke investigation. Measure build, infrastructure startup, migrations,
test execution and cleanup separately before promising performance improvements.

### 2. Separate responsibilities

Classify existing cases into Fast, Persistence, Provider integration, Operator/E2E
and Test infrastructure. Fast tests need no external services. Persistence tests
exercise real EF Core/PostgreSQL behavior. Provider tests exercise external effects.
Operator/E2E tests exercise actual Host, generated clients, published CLI and
restart scenarios. Infrastructure tests exercise provisioning and cleanup of the
test environment itself. Use separate projects where dependency and lifecycle
boundaries justify them; do not build a universal test framework.

### 3. Declare isolation explicitly

Catalog-only cases receive separate databases and may share a server within a
bounded group only after auditing their global effects. Tests of roles, server
settings, global catalogs or external effects use a dedicated server per fixture.
Cases requiring pristine server state use a fresh server or a demonstrated reset
within that fixture. Unique database names alone do not isolate PostgreSQL roles.

### 4. Preserve assertions

Do not weaken absence-of-effects, ownership, transaction, concurrency, recovery or
redaction assertions to accommodate shared infrastructure. Fix isolation first.
Preserve independent arrange/act/assert DbContexts. Do not replace PostgreSQL
semantics with EF InMemory tests.

### 5. Use one primary Linux integration path

GitHub-hosted Linux Actions with a Docker-compatible runtime is the primary full
integration gate. Run tests and containers in the same Linux environment. A
separate Linux runner may serve diagnostics and periodic heavy qualification.
Windows remains a supported development workstation; running the entire suite
locally through a Windows/Linux bridge is not a prerequisite for contribution.

Keep the existing WSLc path until its callers and necessary coverage have migrated.
Do not expand it into a second general-purpose execution system. Removing obsolete
paths requires evidence of replacement, not merely a preferred runtime choice.
This ADR does not authorize SSH setup, runner installation or remote settings
changes as side effects of an ordinary test run.

### 6. Preserve platform coverage deliberately

Windows-specific behavior and published Native AOT CLI checks remain mandatory.
A symmetric full PostgreSQL suite on Windows is not required solely for symmetry.
Map every existing obligation before moving gates; do not disable checks in
anticipation of replacement coverage.

### 7. Bound concurrency by resources

Provide configurable limits for simultaneous PostgreSQL servers and heavy E2E
scenarios, selected from measurements rather than CPU count alone. Preserve
intentional concurrency tests inside their owned environment. Do not hide races
by globally serializing tests or inflating timeouts.

### 8. Provide a checked-in entrypoint and short preflight

Use one documented execution entrypoint with explicit suite selection and a short
preflight: supported SDK/runtime, container availability, native executable when
required, usable source/assets paths, temporary-file semantics and capacity.
Verify creation and removal of one owned server before the full infrastructure
suite. Failed prerequisites produce a visible non-success result before hundreds
of dependent cases fail; missing infrastructure must not silently skip assertions.

### 9. Make paths and filesystem requirements explicit

Architecture tests receive an explicit validated source root rather than relying
on parent traversal from build output. Use embedded or explicitly copied test
assets where source inspection is unnecessary. Linux tests of Unix permissions
use native temporary storage with the required semantics. Do not relax permission
assertions or remount filesystems automatically to make tests pass.

### 10. Give infrastructure one lifecycle owner

Each run has one owner for its containers, processes and temporary credentials.
Cleanup is bounded and limited to positively identified run-owned resources.
Recovery after interruption is explicit. Never use broad prune or affect unrelated
resources; unresolved cleanup remains visible rather than causing endless retries.

### 11. Keep useful, safe diagnostics

Preserve the primary test failure separately from cleanup failure. Ordinary run
evidence records source SHA and dirty state, tool versions, commands, results,
timings and cleanup outcomes. Publish only allowlisted safe diagnostics. Retain
sensitive originals privately with a documented bounded retention and cleanup
policy. Subsequent cleanup must not overwrite original failure artifacts.

### 12. Keep evidence proportional

Ordinary source attribution uses the exact commit, actual source contents, build
and test results. Byte-immutable Git indexes, numerous archive copies and repeated
manual toolchain forensics are not routine prerequisites. Detailed investigation
requires a concrete discrepancy. Preserve dependency/security checks and honest
evidence limits; simplifying the procedure does not make failed checks successful.

### 13. Separate special qualification

Keep mandatory functional coverage. Run heavy infrastructure fault/recovery and
load qualification when the relevant mechanism changes and periodically, with an
explicit trigger/ownership map. Fast deterministic infrastructure checks remain
regular. ADR-055's real Kubernetes mass-start qualification of at least 150 clients
remains required at capability acceptance and after material runtime-path changes.

### 14. Repeat only invalidated evidence

Choose reruns according to changed code, build, executable, environment or missing
results. Report/metadata corrections alone do not require a full repeat. Preserve
previous unsuccessful runs and explain what a later run establishes. Never retry
until green or conceal an environment failure as product success.

### 15. Migrate with a coverage map

Map every existing case and theory parameter to its destination and required gate.
Preserve multiplicity; identical display names do not prove identical cases.
No unexplained omissions, silent skips or replacement of real external guarantees
are permitted. Optimize migration setup or remove duplicate execution only with
evidence that the corresponding behavior remains covered.

## Delivery order and acceptance

The maintainer accepted a stop-loss on 2026-11-13. This supersedes the earlier
ordering that required completion of #191 and then #188 before maintenance.
Stop new qualification attempts for those issues, reconcile active task-owned
processes and safely release positively identified resources. Preserve existing
results and unresolved cleanup; do not restart unavailable infrastructure merely
to continue the previous qualification chain. Keep both issues and their PRs open
and unmerged, with a concise completed/failed/unperformed checkpoint.

Start maintenance from current main, without requiring the unmerged #191 harness:

1. Inventory coverage and provide the minimal runner, preflight and documentation.
2. Separate persistence and Provider-effect isolation; measure and bound resources.
3. Separate Host/E2E and infrastructure qualification, migrate CI and remove only
   demonstrated obsolete execution paths.

Then finish or explicitly reassess #188/#191 using the new execution path before
resuming functional P3. Preserve useful diagnostic and isolation fixes, but do not
retain infrastructure solely because it was already implemented. Any scope
supersession must be recorded explicitly; stopping work is not issue completion.
Known hosted failures remain unresolved until explained and corrected or otherwise
explicitly dispositioned; a runtime change alone is not a waiver. The first slice
must establish a reproducible GitHub-hosted Linux/Docker/Testcontainers run and
actionable diagnostics without depending on local WSLc sessions or a private
supervisor. Do not require the entire reorganization in that first slice.

Acceptance requires a clean-checkout documented Linux run, explicit Windows/AOT
coverage, a complete case/parameter map, useful failure diagnostics, and verified
owned-resource cleanup. The Windows developer must have a documented way to invoke
and inspect the Linux path without rebuilding a cross-system full-suite harness.
Record before/after timings without a speculative speedup target.

Update CONTRIBUTING and runtime guides alongside the working migrations. Existing
required checks remain in force until their replacement is approved and wired.
Do not change branch protection, bypass checks or waive current CI failures through
this ADR. Exact-SHA review and merge authority under ADR-055 remain unchanged.

## Consequences

Maintenance temporarily precedes feature delivery, but reduces repeated setup and
investigation cost. Some catalog tests can share infrastructure; global-effect
tests intentionally retain stronger isolation. Linux becomes the primary full
integration environment while platform-specific guarantees remain explicit.
Test infrastructure and its evidence process must stay smaller than the product
behavior they support, with no new production runtime dependency.
