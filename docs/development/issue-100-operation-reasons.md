# Issue #100: Compatible Provider-neutral operation reasons

Issue: <https://github.com/hamstix/haby/issues/100>

This is the first implementation slice of accepted
[ADR-032](../adr/0032-dependent-operation-plans-and-exact-dispatch.md).
Run coding first, then independent review against the resulting exact PR HEAD.
The maintainer merges after reviewing evidence and required CI gates.

## Coding-agent prompt

```text
Implement https://github.com/hamstix/haby/issues/100 from current origin/main.

Read AGENTS.md, CONTRIBUTING.md, the usable alpha pilot in ROADMAP.md,
ADR-025/026/027/031/032 and the PostgreSQL integration-test guide. Treat the
issue body and ADR-032 reason table as the implementation contract.

Create codex/feat-100-neutral-operation-reasons. Inspect status and current
code before editing. Preserve user changes and existing historical reasons.

Deliver the complete vertical path: new reason categories, explicit persistence
read/write support, additive reason-constraint migration, PostgreSQL mappings,
Host protobuf mappings, CLI text/source-generated JSON and distinguishing tests.
Do not renumber existing CLR/wire values or rewrite historical rows. New wire
numbers are 9/10/11; historical databaseNameInvalid stays value 4 on the wire.

Map new DatabaseNameInvalid/UnsupportedShape conditions to InputInvalid and
broad ProviderInputUnavailable conditions to InputUnavailable. Preserve their
definitive/transient classification, state transitions and cancellation rules.
Keep old ProviderInputUnavailable rows safely resumable. Review every reason
allow-list, recovery projection and retry predicate; changing a presenter alone
does not implement the contract. HandlerUnavailable representation is included,
but do not invent a registry or infer handler absence from arbitrary exceptions.

Keep the single-database admission, dispatch barrier, uncertainty policy,
manual recovery and AppliedGeneration behavior. Do not implement the graph,
roles, grants, generated credentials, configuration delivery or extra effects.
Follow ADR-026: no new stored workflow or role separation. Migration rollback
must refuse when new reason rows exist, preserving data and the current schema.

Use focused tests with observable counterexamples for each behavior change.
Use real disposable PostgreSQL for migration/constraint/restart behavior and
separate DbContexts for arrange/act/assert. Prove historical wire/JSON output
and published Native AOT CLI behavior as well as new-code output. Do not
access a persistent developer database or copy secrets into fixtures/reports.

Run focused checks and applicable full solution, PostgreSQL, formatting, diff
and Native AOT gates documented in the repository. Use the WSLc integration
helper if local Docker is unavailable. Distinguish environment blockers from
code failures; report counts, warnings and CI on the exact final HEAD.

You may edit scoped files, commit, push this topic branch and open a draft PR
targeting main with Closes #100, Refs #91 and Refs #86. Do not merge,
force-push, change remotes or close #91/#86. Report exact HEAD, PR URL,
compatibility/migration behavior, validation and remaining limitations.
```

## Review-agent prompt

```text
Perform an independent, read-only review of PR <PR_URL> against
https://github.com/hamstix/haby/issues/100, AGENTS.md, CONTRIBUTING.md,
ROADMAP's usable alpha pilot and accepted ADR-025/026/027/031/032.

Create the external report at the start and update it incrementally:
D:\Projects\hamstix\tmp\code-reviews\PR<pr-number>_neutral-operation-reasons.md
Only that external report may be written. Do not edit tracked files, commit,
push, post comments or merge. Record the exact reviewed HEAD/base and recheck
HEAD before the final verdict. Use an isolated temporary checkout if needed.

Prioritize these issue-specific checks:
1. Trace each new reason from resolver/target through journal, PostgreSQL
   constraint, restart reader, Host protobuf and CLI text/JSON. Confirm the
   actual emit path, not only synthetic presenter tests.
2. Verify all historical strings and wire numbers 0-8, particularly value 4
   and databaseNameInvalid, remain readable/renderable without row rewriting.
3. Check transient versus definitive handling. A new InputUnavailable must
   preserve safe pre-dispatch resume, and old ProviderInputUnavailable rows
   must not become stuck. No reason may authorize a post-dispatch retry.
4. Check recovery inspection and journal reason allow-lists. Unknown stored
   values fail safely, not as success or a default category. Do not treat
   deliberate valid administrator DB edits as a new integrity threat.
5. Review fresh/upgrade/downgrade migration tests against real PostgreSQL.
   A rejected downgrade preserves history and schema atomically. No merged
   migration edits, new workflow procedures/triggers or database role split.
6. Check Provider neutrality: common reasons contain no database names or
   adapter fields; PostgreSQL name validation stays adapter-owned. This slice
   does not require a second executor or generic dispatch framework. Missing
   handler support is not guessed from an arbitrary exception.
7. Verify dispatch barriers, historical recovery and AppliedGeneration did not
   change. Check focused distinguishing tests, secret redaction, generated
   JSON metadata and published CLI AOT coverage on the reviewed SHA.

Inspect the actual diff, changed tests and CI. Report actionable P0-P3 findings
with exact file/line, violated requirement, concrete scenario and the smallest
useful correction/test. Distinguish confirmed defects, questions and unverified
risks. Do not turn style preferences into blockers or green CI into proof.
Finish with an explicit merge-readiness verdict and unperformed checks.
```

## Completion and next step

The issue is complete when the scoped PR is merged after accepted evidence and
required CI. This finishes only ADR-032's reason slice. Keep #91 and #86 open
for the remaining boundaries until their tracking disposition is explicit.
Next prepare exact typed admission/dispatch for the existing database action;
then implement the graph and aggregate completion before admitting new effects.
