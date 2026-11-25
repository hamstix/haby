# Nightly PostgreSQL and exact merge selection (#297)

This implementation separates targeted merge evidence from full release/install
qualification under [ADR-061](../adr/0061-focused-development-and-final-qualification.md).
Policy acceptance alone does not deploy it. Its own candidate must pass the old
full gates and exact independent review/architect approval before installation.
No repository settings change is included or necessary for the observed settings.

## Routes and exact identity

Ordinary PR Build keeps fast, dependency review, both published Native AOT targets
and current other required contours. PostgreSQL uses the complete immutable
base-to-tested-source diff and current case discovery. `Merge qualification`
uses the control installed on main, maintainer dispatch, current HEAD/base/ordered
test merge/tree admission, exact artifacts and a final freshness check. Its control
artifact says `merge-only-not-release`; no targeted pass can satisfy full Final.
Missing, failed, canceled, skipped, stale or unaccounted results fail closed.

`Final qualification` retains full release/real-installation scope and all existing
PostgreSQL, AOT and applicable API/Kubernetes obligations. Its default reducer does
not accept targeted evidence. New source requires new applicable exact evidence;
a green main or ancestor nightly never grants installation readiness.

Ordinary main pushes do not launch a duplicate full PostgreSQL regression. The
scheduled workflow captures main SHA once (01:17 UTC nightly), runs the existing
Build full PostgreSQL entrypoint and verifies both functional and infrastructure
inventories, original primary failure and cleanup. It retains existing AOT/fast
Build work rather than introducing a second harness. Main may advance while it
runs; its report keeps the captured SHA. Manual invocation uses the same route;
non-main execution requires explicit rehearsal and exact expected_head, and is
labeled non-release. Before the new workflow exists on main, the existing
Infrastructure qualification dispatch offers nightly_full_rehearsal=true with
expected_head and empty testcase/testgroup; it calls the SAME reusable nightly
workflow instead of its ordinary infrastructure-only job. Coordinator checks
run/ref/SHA deduplication before its one authorized dispatch; uncertain writes
are reconciled read-only, never automatically repeated.

## Fixed smoke and relevant scope

Smoke is exactly these two real PostgreSQL facts, versioned by method and case in
`scripts/postgresql-relevance.mjs`:

- ManifestCatalogPersistenceTests.Migration_and_fresh_provider_preserve_complete_typed_revision_and_original_history.
- OperatorHostIntegrationTests.Explicit_execution_uses_production_singleton_and_pinned_inputs_and_survives_restart.

The first proves catalog initialization/fresh-provider immutable history; the
second proves authenticated public durable execution/restart. Neither replaces
feature-specific, authority, concurrency or infrastructure coverage. Missing or
changed smoke identity requires reviewed renewal and fails admission.

Initial useful production narrowing covers only these exact CLI command files:
`Commands/Environments/EnvironmentCommands.cs`,
`Commands/Placements/PlacementCommands.cs`, and `Commands/Manifest/ValidateCommand.cs`
under `src/habyctl`. Only literal-only changes preserve the audited leaf boundary;
new calls, structural edits or shared/context/auth/contract/presenter/dependency
changes visibly broaden to full. This conservative limitation is deliberate,
not general fast coverage for arbitrary CLI changes.

The relevant native-consumer union comprises ApplicationConfigurationTests,
ApplicationGenerationTests, PostgreSqlApplicationLoginTests,
ApplicationLoginLoggingTests, DurableDeploymentPlanningTests,
OperatorHostIntegrationTests, OperatorProviderAccessTests and
PostgreSqlPairImportTests. These include partial/nested helpers and every concrete
ApplicationLoginScenario descendant. Current inventory is 852 rows plus catalog
smoke=853 of1381; this is relevant coverage, not an expanded smoke or a count cap.
New native consumers/subclasses force full until audited; new rows in existing
classes join current discovery automatically. No runtime speedup is promised.

Test-only changes select the actual sealed class including all partial/theory
rows plus smoke only when no ambiguous/shared/base/helper declaration is edited.
New classes/renames/helper/unknown paths conservatively use full. Migrations,
transactions, locks, secrets and shared persistence remain full. Narrowing more
production files is an incremental reviewed change, not a new platform prerequisite.

## Local commands and evidence

Existing full invocation remains:

```powershell
./scripts/test-postgresql-measured.ps1 -Docker -Suite full -AnonymousReflectionPrecheck -ResultsDirectory .artifacts/pg-full-new
```

The selector creates a source/tree-bound JSON record from complete Git objects;
there are no author-supplied filter inputs to trusted Merge control:

```powershell
node ./scripts/postgresql-relevance.mjs $PWD $base $candidate .artifacts/pg-selection.json
# Only a targeted record is passed to -SelectionPath; full uses the command above.
./scripts/test-postgresql-measured.ps1 -Docker -SelectionPath .artifacts/pg-selection.json -ResultsDirectory .artifacts/pg-targeted-new
```

Selection before owned setup and live discovery/execution reconciliation retain
full theory multiplicity, resource limits and primary/cleanup outcomes. Targeted
coverage reports `focused`, never `complete/full`. WSLc supports the same selection
for local focused-only evidence; it does not replace hosted qualification.

Cheap controls: node tests for routing/relevance/Merge/Final, plus
`test-postgresql-union.ps1`, existing focused/suite controls and solution checks.
Representative targeted and pinned nightly runtime evidence must identify source,
route, inventory, failures and cleanup separately; no Cartesian campaign or
repeated unchanged run until green. Artifacts are acquired once and shared.

## Failure ownership and transition

Missing/canceled/incomplete nightly is not green. Triage in the next development
cycle and prioritize reproduction/correction. Confirmed data-integrity or
authorization regressions block affected work and release/installation; unrelated
work is not automatically frozen. Record scope/disposition; unknown causes stay
unknown. Preserve failed originals and distinguish venue from code failure.

The authenticated preparation snapshot observed classic protection404, only
creation/deletion effective rules and a disabled main ruleset with no required
status checks. This is no waiver of coordinator mandatory checks. Remote settings
delta is NONE: do not activate a broad ruleset or alter bypass actors. Future
mechanical enforcement needs a fresh exact maintainer-approved proposal and valid
published context first. No protection/settings mutation is authorized here.

After verified merge/install of #297 control, coordinator acceptance switches to
Merge qualification for integration while Final remains release/install. Until
then, including the implementation candidate, previous full gates remain required.
Independent exact-SHA review and architect approval remain mandatory throughout.
#275 Reference retirement and #292 remain separate pending capabilities.
