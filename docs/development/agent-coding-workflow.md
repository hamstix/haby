# Agent-assisted development workflow

- Status: Active maintainer process
- Initial scope: M2.1 manifest foundation
- Audience: maintainers coordinating one or more coding agents

## Purpose

This guide describes how a human maintainer prepares, delegates, reviews and
merges an agent-implemented development slice. Architecture remains controlled
by the maintainer and the accepted repository documents. Agents accelerate
specification checks, implementation and review; they do not silently make new
product or public-contract decisions.

The process is optimized for two complementary agents:

- a fast implementation agent with a limited token budget;
- a slower long-context agent with a large or effectively unlimited token
  budget.

The roles are capabilities, not permanent identities. A maintainer may swap the
agents when a particular task fits the other one better.

The recommended default delegation boundary is:

```text
Agent: branch -> implementation -> validation -> commit -> push -> draft PR
Human: scope approval -> review decisions -> merge authorization -> next slice
```

This removes routine remote operations from the maintainer without delegating
the irreversible product decision to merge a change into `main`.

## Core rules

1. One pull request delivers one coherent outcome.
2. One agent owns the write branch for that pull request.
3. A different agent reviews the result before merge when practical.
4. Accepted ADRs and architecture contracts are the source of truth. Chat
   history is not part of the implementation contract.
5. A dependent coding slice starts only after its predecessor is merged into
   `main` and required checks pass there.
6. Specification work for the next slice may run in parallel with implementation
   of the current slice.
7. An agent stops and asks the maintainer when implementation requires a new
   architectural choice or scope expansion.
8. The default branch remains buildable. Incomplete internal foundations may be
   merged only when they are isolated, tested and do not expose misleading
   public behavior.
9. Haby uses GitHub terminology: changes are submitted as pull requests, not
   GitLab merge requests.
10. Agents never receive credentials in prompts, fixtures, logs or committed
    files.

## Roles

### Human maintainer

The maintainer owns:

- milestone and slice boundaries;
- acceptance of architectural decisions;
- prioritization and sequencing;
- approval of new dependencies;
- authorization for Git mutations and remote operations;
- classification of review findings;
- the final merge decision.

The maintainer does not need to perform routine Git operations manually when an
agent has the necessary tools. The maintainer must nevertheless verify the
branch, pull request, checks and final scope before merge.

### Implementation agent

The implementation agent owns the short feedback loop:

```text
inspect -> test baseline -> edit -> build -> test -> inspect diff
```

It may create a branch, commit, push and open a draft pull request only when the
task explicitly authorizes each of those operations. It does not merge its own
pull request unless the maintainer gives a separate explicit instruction after
review and CI.

### Specification and review agent

The long-context agent normally works read-only. It owns:

- consistency checks across the relevant ADRs and architecture documents;
- field, schema and DTO parity matrices;
- positive and negative test-case inventories;
- review of a completed diff against accepted invariants;
- identification of missing tests, unintended coupling and scope drift.

It reports findings to the maintainer or as pull-request review comments. It
does not push fixes to the implementation branch unless the maintainer
explicitly changes its role and the current branch owner hands off ownership.

## Workflow artifacts

Each coding slice has these artifacts:

1. **Slice issue or task card** — scope and acceptance contract.
2. **Topic branch** — one short-lived branch created from current `main`.
3. **Implementation handoff** — summary of changes, tests and limitations.
4. **Draft pull request** — the visible review boundary.
5. **Independent review report** — findings tied to files, tests or contracts.
6. **Merged pull request** — squash-merged permanent result.

A GitHub issue is recommended for every implementation slice. A small
documentation-only correction may use a task card in the coordinating session,
but the acceptance contract must still be explicit.

## Slice state model

```text
Proposed
  -> Ready
  -> Specified
  -> In progress
  -> Draft PR
  -> In review
  -> Merge ready
  -> Merged
  -> Verified on main
```

The maintainer advances a slice only when the exit criteria for its current
state are satisfied.

## Phase 0: Prepare the milestone

This phase is performed once before delegating the first slice of a milestone.

### Maintainer actions

1. Confirm that the relevant architecture decisions are accepted.
2. Identify unresolved dependency or implementation choices.
3. Divide the milestone into reviewable dependent slices.
4. Define which slices may be specified in parallel and which coding work must
   remain sequential.
5. Create a milestone or tracking issue when GitHub project management is in
   use.

### Exit criteria

- the next slice has no unresolved product decision;
- its predecessor relationship is known;
- the required documents are identified;
- the result can be tested without implementing a later slice;
- out-of-scope work is explicit.

If these criteria are not satisfied, continue architecture discussion instead
of starting a coding agent.

## Phase 1: Write the slice task card

The maintainer creates one task card using the template at the end of this
document. The card is the agent's execution contract.

### Required contents

- one-sentence goal;
- exact branch name;
- required reading;
- concrete deliverables;
- invariants that must remain true;
- explicit out-of-scope list;
- acceptance tests;
- validation commands;
- stop conditions;
- granted Git and GitHub permissions;
- required final handoff format.

Required reading should be narrow. The implementation agent normally receives
`AGENTS.md`, `CONTRIBUTING.md`, the relevant ROADMAP section and only the ADRs
that govern the slice. Do not send the complete conversation transcript.

### Definition of Ready

A task card is ready only when the maintainer can answer yes to all of these:

- Does it describe one observable or testable outcome?
- Can unrelated future functionality be omitted?
- Are the public types and behaviors already decided?
- Are dependency additions either forbidden or explicitly subject to approval?
- Can the agent know when it must stop rather than improvise?
- Can a reviewer determine completion from the diff and tests?

## Phase 2: Prepare specification and tests one step ahead

For contract-heavy slices, the maintainer sends a read-only preparation task to
the long-context agent before implementation starts.

### Typical outputs

- contract or field matrix;
- expected source-generated DTO inventory;
- valid and invalid example matrix;
- diagnostic code and path expectations;
- dependency and license questions;
- possible contradictions between documents;
- proposed review checklist.

The output is advisory until the maintainer accepts it. If the analysis exposes
a missing architectural decision, the maintainer resolves and documents that
decision before marking the coding slice Ready.

### Exit criteria

- the preparation report contains no unresolved blocking question; or
- every blocking question has been decided and reflected in the task card or an
  accepted document;
- expected tests are concrete enough for the implementation agent to write.

## Phase 3: Create an isolated implementation workspace

Only the implementation agent writes the slice branch.

### Preferred agent authorization

The task should explicitly say something equivalent to:

> You are authorized to create branch `feat/...` from the current `main`, edit
> the scoped files, create commits, push that branch and open a draft GitHub pull
> request. Do not merge the pull request, force-push, modify remote settings or
> change files outside the stated scope.

Without this authorization the repository rules require the agent to stop
before commit, push or pull-request creation.

### Workspace rules

- If both agents use the same clone, create a separate Git worktree for every
  writable branch.
- Never point two writable sessions at one worktree.
- Start the branch from the latest `origin/main`.
- Verify that the worktree is clean before editing.
- Preserve unrelated user changes; do not reset or discard them.

### Agent preflight

The implementation agent must:

1. read repository instructions and required documents;
2. inspect Git status and the actual project structure;
3. fetch or otherwise verify the current base when network access is authorized;
4. run the smallest useful baseline build or test;
5. report any baseline failure separately from implementation failures.

### Exit criteria

- the branch and base commit are known;
- the worktree contains no unrelated changes;
- baseline status is recorded;
- the agent has not discovered a contract blocker.

## Phase 4: Implement the slice

The agent implements the smallest complete change described by the card.

### Agent behavior

- Add focused tests together with new behavior.
- For regressions, add a failing test before changing production code.
- Keep domain code independent from hosting, persistence, UI and plugin
  implementations.
- Do not add compatibility adapters for the unused prototype unless explicitly
  requested.
- Do not broaden identifiers, DTOs or interfaces for hypothetical future use.
- Do not mark a ROADMAP item complete when only one part of it is implemented.
- Keep expected failures as typed results where accepted decisions require them.

### Mandatory stop conditions

The implementation agent stops and reports the issue when it discovers that the
slice requires:

- a change to an accepted manifest or application contract;
- a new runtime dependency that was not approved;
- a choice between materially different persistence or API semantics;
- weakening validation, redaction or immutability rules;
- changes in a later roadmap milestone;
- edits to unrelated prototype behavior;
- destructive Git operations;
- credentials or unavailable external infrastructure.

The maintainer either updates the task card, records a decision or postpones the
new requirement. The agent does not choose silently.

## Phase 5: Validate locally

The implementation agent runs focused validation first and full applicable
validation last.

For the current repository the solution-level commands are:

```powershell
dotnet restore .\HamstixHaby.slnx
dotnet build .\HamstixHaby.slnx --no-restore
dotnet test .\HamstixHaby.slnx --no-build
git diff --check
```

Additional slice-specific tests belong in the task card. A documentation-only
slice may replace .NET validation with link and Markdown checks when no code or
project file changed.

### Agent handoff before remote operations

The agent reports:

- files and behavior changed;
- tests added;
- exact commands and results;
- baseline or environmental failures;
- known limitations;
- final `git status` and diff summary.

### Local completion criteria

- focused tests pass;
- applicable solution build and tests pass;
- `git diff --check` passes;
- no untracked or unrelated file is accidentally included;
- the diff matches the task card;
- failures caused by restore, network or environment are explicitly separated
  from code failures.

## Phase 6: Commit, push and open a draft pull request

When authorized, the implementation agent performs these actions itself:

1. inspect staged and unstaged changes;
2. stage only the intended files;
3. create an English Conventional Commit;
4. push the topic branch without force;
5. open a draft GitHub pull request against `main`;
6. return the pull-request URL and final branch state.

Prefer an authenticated GitHub MCP or app integration for issue and pull-request
operations when one is connected; otherwise use the GitHub CLI. The local Git
client normally pushes the branch unless an integration provides an equivalent
operation. If no authenticated tool is available, the agent stops after the
local commit and gives the maintainer the exact branch, title and pull-request
description to use manually.

The pull request contains:

- problem and motivation;
- chosen implementation boundary;
- tests and validation;
- known limitations and explicit follow-up work;
- compatibility impact;
- linked issue, using `Closes #...` where appropriate.

### Maintainer checkpoint

The maintainer verifies:

- the PR targets `main`;
- the branch name and title follow `CONTRIBUTING.md`;
- the changed-file list matches the task card;
- the PR remains Draft until independent review begins;
- no secrets, private endpoints or unrelated product details appear.

## Phase 7: Run independent review

The maintainer gives the pull-request diff and original task card to the review
agent. The review task is read-only unless explicitly stated otherwise.

### Review order

1. Correctness against acceptance criteria.
2. Compliance with ADRs and architecture dependencies.
3. Missing failure, concurrency and boundary tests.
4. Public contract and compatibility impact.
5. Secret redaction and diagnostic safety.
6. Cancellation, idempotency and deterministic behavior where applicable.
7. Unrelated changes and premature abstractions.
8. Maintainability and naming.

Review findings should identify a file and precise behavior. Style preferences
without correctness or maintainability impact should not block the slice.

### Finding severity

- **P0** — security, data loss, secret disclosure or destructive correctness
  failure; merge is forbidden.
- **P1** — accepted contract violation, broken invariant or missing essential
  test; merge is forbidden.
- **P2** — maintainability or robustness issue that should normally be fixed in
  the current PR.
- **P3** — optional improvement or future follow-up; does not block merge when
  explicitly recorded.

### Exit criteria

- every P0 and P1 finding is resolved;
- every P2 finding is fixed or consciously reclassified by the maintainer;
- P3 follow-ups are captured when worth retaining;
- the review agent has rechecked the final diff when fixes were material.

## Phase 8: Apply review fixes

The maintainer sends the findings back to the original implementation agent.
That agent keeps branch ownership, applies focused fixes, reruns validation,
commits and pushes normally.

Do not let the reviewer and implementer race to modify the same files. If branch
ownership must change, stop the original session, record the current branch
state and explicitly hand ownership to the new agent.

After fixes, the implementation agent updates the PR summary when scope or
validation changed materially.

## Phase 9: Declare the pull request merge-ready

The maintainer converts the draft to a ready pull request only when all of these
are true:

- scope matches the accepted task card;
- local validation is successful or an explicitly accepted environmental
  exception is documented;
- required GitHub checks are green;
- independent review has no unresolved P0, P1 or unaccepted P2 findings;
- every review conversation is resolved or deliberately retained;
- documentation and tests describe the implemented behavior;
- compatibility and follow-up notes are accurate;
- the branch is based on a sufficiently current `main` and has no unexpected
  conflicts.

Green CI alone does not make a pull request merge-ready.

## Phase 10: Merge and verify

The maintainer performs or explicitly authorizes the final merge. Haby uses
squash merge into `main` to keep a linear default-branch history.

An implementation agent must not interpret permission to push or open a pull
request as permission to merge it.

### Post-merge actions

1. Verify the pull request was squash-merged into `main`.
2. Verify required checks on the resulting `main` commit.
3. Close the slice issue if it was not closed automatically.
4. Delete the remote topic branch.
5. Remove or archive the local worktree only after its changes are safely merged.
6. Update the maintainer's primary checkout from `main`.
7. Confirm that no required follow-up exists only in chat history.
8. Re-evaluate the next task card against the code that was actually merged.

### Definition of Done

A slice is Done only when:

- its pull request is merged;
- the result exists on `main`;
- `main` checks pass;
- the issue and meaningful follow-ups are recorded;
- temporary branch/worktree state is cleaned up or intentionally retained;
- the next dependent slice can start from `main` without copying unmerged work.

## Handling failures and blocked work

### Baseline failure

If the same failure exists before the slice, record it as baseline behavior. Do
not silently fix it in an unrelated PR or claim the new code caused it.

### Restore, network or permission failure

Retry only when allowed and useful. Report the exact command and distinguish an
unverified build from a compiler or test failure. Do not weaken code to work
around unavailable infrastructure.

### Architecture blocker

Pause coding, return the precise question and list affected ADRs or contracts.
The maintainer resolves the decision before work resumes.

### Oversized diff

If the agent discovers that the slice contains multiple independently valuable
outcomes, stop before opening the final PR and propose a split. Do not hide scope
growth behind one broad title.

### Review disagreement

The maintainer decides whether a finding identifies an accepted invariant, a
real implementation risk or a preference. Material new semantics require a
documented decision rather than an informal review compromise.

## Parallel scheduling with two agents

Safe parallelism is:

```text
Long-context agent: specify slice N+1 or review slice N
Implementation agent: implement and fix slice N
Human maintainer: approve decisions and merge slice N
```

Unsafe parallelism includes:

- two agents editing one branch;
- coding slice N+1 against unmerged internal APIs from slice N;
- reviewer fixes pushed while the implementation agent is still editing;
- two dependent PRs both presented as independently mergeable;
- allowing the next agent to rely on chat-only assumptions.

Independent coding slices may run concurrently only when neither changes the
other's contracts, project files or shared infrastructure and each can merge in
either order.

## Initial M2.1 execution wave

The first execution wave contains three sequential pull requests. Its purpose is
to establish a deterministic manifest input boundary before implementing domain
normalization, configuration rendering or immutable import storage.

### Slice 1: M2 core project foundation

- Branch: `feat/m2-core-foundation`
- Primary agent: fast implementation agent
- Parallel long-context work: prepare the schema/DTO matrix for Slice 2

Deliverables:

- create `src/Haby.Domain`, `src/Haby.Manifest` and `src/Haby.Application`;
- create their three focused test projects;
- add them to `HamstixHaby.slnx` with the ADR-013 dependency direction;
- implement only the identity and version value objects needed by the next
  slice;
- add focused invariant, equality and UUIDv7 tests;
- prove that new core projects do not reference hosting, EF Core, UI or plugin
  implementations.

Out of scope:

- JSON DTOs and schema;
- parsing and diagnostics;
- repositories and import services;
- speculative Environment, ChangeSet or Provider implementation.

Completion gate:

- all six projects build and test in the solution;
- dependency direction matches ADR-013;
- every introduced value object has invariant and equality tests;
- no existing prototype project is removed or migrated.

### Dependency decision before Slice 2

The maintainer confirmed Corvus.Text.Json.Validator 5.7.7 for JSON Schema
Draft 2020-12 parity validation in Slice 2. JsonSchema.Net's related binary
packages use OSMF terms that do not fit Haby's intended commercial use. The
restored .NET 10 package graph (`Corvus.Text.Json.Validator`, `Corvus.Text.Json`
and `NodaTime`) declares Apache-2.0 for every binary package. A .NET 10 compile
spike confirmed offline
evaluation, safe instance/schema paths and the accepted high-precision cases.
Corvus treats `-0` and `0` as distinct under `uniqueItems`.
Haby therefore applies exact-number equality to the v1alpha1 integer and number
parameter `allowedValues` arrays at the schema-validation boundary; the
checked-in schema and input JSON remain unchanged. The decision covered:

- full Draft 2020-12 support;
- deterministic access to keyword and instance paths;
- ability to suppress library-specific messages at the Haby boundary;
- .NET 10 support;
- trimming/AOT characteristics relevant to libraries;
- license and maintenance status;
- testability without network access.

Trimming and Native AOT behavior were not verified in Slice 2; no Native AOT
support is claimed for the server.

The implementation agent must verify the package API, .NET 10 restore/build,
schema evaluation, error-path access, offline testability and exact numeric
equality in a throwaway compile spike. Pin the verified version in central
package management. Document any trimming limitations without claiming Native
AOT support for the server.

### Slice 2: v1alpha1 manifest contract

- Branch: `feat/m2-manifest-contract`
- Primary agent: fast implementation agent
- Reviewer: long-context agent using the accepted parity matrix

Deliverables:

- implement the complete source-generated DTO graph for
  `haby.dev/v1alpha1`;
- include the typed import-envelope DTO in that source-generated graph; raw
  UTF-8 envelope parsing and envelope validation belong to Slice 3;
- add explicit discriminators and permitted `JsonElement` boundaries; configure
  metadata-mode deserialization so discriminator members may appear after
  ordinary object members;
- enforce required-member presence in DTO metadata (for example, with
  `[JsonRequired]`); CLR non-nullability alone is insufficient;
- use an exact JSON-number wire type for `number` parameter constraints, with
  a metadata-compatible converter; do not pass through `double` or `decimal`;
- check in the complete Draft 2020-12 schema under its accepted canonical ID;
- add positive examples for every accepted manifest shape;
- add schema/DTO parity tests, including absent and empty Resource lifecycle,
  explicit `null` rejection, required-member presence and discriminators after
  ordinary object members.

Out of scope:

- full validation orchestration and diagnostic aggregation;
- normalized domain declaration;
- semantic reference resolution;
- Liquid execution and Configuration composition;
- ManifestRevision creation.

Completion gate:

- the field matrix, schema and DTOs agree;
- DTO deserialization uses metadata-based source generation;
- absent lifecycle, empty lifecycle and explicit `retain` yield the same core
  default in the contract tests; explicit `null` and unknown members fail;
- no reflection-based type discovery or shape guessing exists;
- the checked-in schema is complete rather than a temporary partial contract;
- focused and solution tests pass.

### Slice 3: deterministic manifest parser

- Branch: `feat/m2-manifest-parser`
- Primary agent: fast implementation agent
- Parallel long-context work: prepare and review the negative diagnostic corpus

Deliverables:

- accept the original UTF-8 envelope payload;
- reject BOM, invalid UTF-8, malformed JSON, duplicate properties and excessive
  depth;
- validate the closed import envelope;
- dispatch exact `apiVersion` and `kind`;
- run the checked-in schema and source-generated deserialization;
- return stable envelope errors and manifest validation reports;
- implement deterministic diagnostic ordering, deduplication and truncation for
  the stages present in this slice;
- add the shared negative corpus for those stages.

Out of scope:

- semantic local-reference resolution;
- normalized domain mapping;
- extension-contract catalogs;
- import stores and Manifest revisions;
- REST and gRPC adapters.

Completion gate:

- equivalent in-process callers receive identical reports;
- duplicate properties are detected before DTO deserialization;
- diagnostics use accepted codes and pointer roots;
- library-specific errors and messages do not escape;
- parser tests cover boundary and truncation behavior;
- focused and solution tests pass.

After Slice 3 is verified on `main`, the maintainer performs an architecture
checkpoint and prepares the next wave from the real implementation. The next
wave is expected to cover normalized declarations, semantic references,
contract metadata, Configuration values, Parameters, immutable import and later
ChangeSet planning. Those boundaries are not delegated until the checkpoint.

## Task-card template

````markdown
# Goal

<One independently reviewable result.>

# Branch

`feat/...`

# Base

Current `origin/main` after <required predecessor PR> is merged.

# Required reading

- `AGENTS.md`
- `CONTRIBUTING.md`
- `ROADMAP.md`, <exact section>
- <relevant ADRs and architecture contracts only>

# Deliverables

- <specific type, behavior or artifact>
- <required tests>

# Invariants

- <rules that must remain true>

# Out of scope

- <forbidden adjacent work>

# Acceptance tests

- <scenario and expected result>

# Validation

```powershell
dotnet restore .\HamstixHaby.slnx
dotnet build .\HamstixHaby.slnx --no-restore
dotnet test .\HamstixHaby.slnx --no-build
git diff --check
```

# Stop conditions

Stop and ask before changing an accepted contract, adding an unapproved
dependency, expanding into another milestone or using destructive Git actions.

# Granted operations

You are authorized to create the named branch, edit scoped files, commit, push
the topic branch and open a draft GitHub pull request. Do not merge, force-push,
modify remotes or edit unrelated files.

# Required handoff

Report the outcome first, then changed files, tests, validation results, known
limitations, commit, branch and pull-request URL.
````

## Read-only review prompt template

```markdown
Review pull request <URL> against issue <URL or task card> and the listed ADRs.
Do not modify files, push commits or broaden the scope. Inspect the actual diff
and tests. Report only actionable findings, ordered by P0-P3 severity, with exact
file locations and the violated invariant. Check correctness, architecture
direction, contract compatibility, deterministic behavior, cancellation,
redaction, missing tests and unrelated changes. If no blocking issue exists,
state that explicitly and list any residual risks separately.
```
