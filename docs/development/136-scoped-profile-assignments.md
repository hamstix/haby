# Scoped Profile assignment administration

Issue #136 implements [ADR-039](../adr/0039-scoped-profile-assignment-administration.md)
through the internal alpha gRPC Host and Native AOT `habyctl`. Use the existing
[isolated Host setup](alpha-operator-local.md) and [Profile import commands](134-profile-operator-path.md).
The trusted administrator consents to distributing every declared secret to the
receiving scope, including future inheriting Components. Assignment stores only
references; it does not look up named secrets or read material.

## Operator path

Create the Environment and import every exact Profile revision referenced by Use.
An Application target requires an imported Application, with no deployment.
Component apply additionally requires an explicit accepted ApplicationVersion
containing that Component. Import order and version ordering never select it.

Use the [Environment example](../examples/configuration-profiles/environment-assignment.json).
[Component replacement](../examples/configuration-profiles/component-assignment.json)
and [empty Application inheritance](../examples/configuration-profiles/inherit-assignment.json)
are also available. Alternatively save this portable declaration as `component.json`:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "ConfigurationProfileAssignment",
  "spec": {
    "target": { "kind": "component", "applicationId": "processor-service", "componentRef": "api" },
    "decisions": [
      { "kind": "use", "profileRef": { "profileId": "serilog-defaults", "revision": 4 } },
      { "kind": "disable", "profileId": "optional-telemetry" }
    ]
  }
}
```

```powershell
habyctl profile assignment validate component.json
habyctl profile assignment apply component.json --environment production --application-version 2.4.0 -o json
habyctl profile assignment get --environment production --application processor-service --component api -o json
habyctl profile assignment get --environment production --application processor-service
habyctl profile assignment get --environment production
```

`--config`/`--context` select the registry and default Environment, with
`--environment` overriding the context Environment. Validate checks only portable
JSON without storage/reference lookups. Apply takes its target from JSON; get
uses explicit options. `--component` requires `--application`. ApplicationVersion
is required only for Component apply and rejected for other apply targets. Get
requires no version, including when later revisions omit the Component.

Apply replaces all local decisions, at most 64 unique ProfileIds, independent of
order. Use requires an exact readable Profile revision; Disable may name an absent
ID. Omission inherits. Use replaces the inherited whole Profile revision; Disable
suppresses it. Set `decisions` to `[]` to restore inheritance. This retains a
persisted empty record with its token/audit. There is no delete API.

## Concurrency and uncertain outcomes

Default apply asks the server to validate the portable declaration, reads exact
local state, then sends ExpectedAbsence or its returned concurrency token. To use
an already inspected precondition explicitly:

```powershell
habyctl profile assignment apply component.json --environment production --application-version 2.4.0 --if-absent
habyctl profile assignment apply component.json --environment production --application-version 2.4.0 --if-match <concurrency-token>
```

The flags are mutually exclusive and bypass automatic validation/read acquisition;
the server still performs all authoritative checks. A matching token with equal
decisions returns `unchanged`, retaining token, actor, UTC time and checked revision.
A real replacement returns `updated` with a fresh token and last-change audit.
Initial acceptance returns `created`, including an empty declaration. ExpectedAbsence
against an existing row and any stale token return `conflict`, even for equal content.
There is no automatic conflict retry. Inspect and deliberately reconcile conflicts.

Cancellation or a lost response does not prove rollback. Read the exact scope and
compare decisions. Equality proves current state, not which request wrote it or
an original request receipt. Do not blindly resubmit. The store checks cancellation
before commit, then waits for commit confirmation without caller cancellation;
confirmed outcomes are preserved. Full request receipts/history are deferred.

Get returns `found` with local target/decisions, stable Environment record identity,
opaque token and last-change audit, or `notFound` for absent local state. Component
audit includes the exact ManifestRevisionRef checked on the last actual change.
A different valid validation version on Unchanged does not rewrite that audit.
Missing Environment/Application/version/Component/Profile references and invalid
context are distinct typed refusals. Corrupt, unsupported or unavailable storage
fails safely as infrastructure failure, never absence, empty success or repair.

Text/JSON use existing exits: 0 success, 1 expected refusal/conflict/absence,
2 usage/configuration/file failure, 3 transport/server failure. JSON results are
one format-version-1 stdout envelope; usage/transport failures use stderr.
Reply validation completes before output. Source bytes retain BOM, which the
server rejects. Existing file/message/depth limits apply. Results and diagnostics
contain no Profile values/options/templates, secret selectors, snapshots or material.

## Persistence and evidence

`configuration_profile_assignments` has a unique composite scope key using the
stable Environment record ID and exact logical target. Strict internal snapshot
format/model 1 stores normalized decisions. EF concurrency-token predicates and
transactions enforce full-set compare-and-swap, including an atomic check for
Unchanged. A same-token no-op update serializes that check without changing audit.
Only the named unique-key violation and EF concurrency failures become conflicts;
other failures propagate to the safe Host boundary. No functions/triggers or
Provider-specific fields are added. The additive migration upgrades current main
ending in `20261105151707_PersistConfigurationProfileRevisions`; no reset is required.
Startup never deletes persistent data. Use matching alpha Host/CLI builds.

Tests exercise generated client -> actual Host -> Application -> disposable
PostgreSQL for all three targets, out-of-order exact versions, stable Unchanged
audit, reference refusal, missing secrets without secret reads, restart and real
`pg_dump`/`pg_restore`. Synchronized store tests cover insertion/update races,
precondition-before-equality, non-recycled tokens, empty state and cancellation/
lost commit responses with independent contexts. Persisted decisions and exact
Profile reads feed the existing expander and two synthetic output families through
the real composer. These are test families, not another production Provider.
Published CLI tests exercise text/JSON, context override, versions, preconditions,
conflict, BOM, exits and no automatic retry. The required Windows/Linux AOT jobs
include assignment command checks; PostgreSQL CI runs the native binary against
the actual Host. Run CONTRIBUTING's solution validation, EF pending-model check
and the complete disposable PostgreSQL suite; Windows can use the WSLc helper.

Planning snapshots/fingerprints, rollout/generation mutation, effective preview,
rendering, materialization, secret resolution/delivery/authorization, selectors,
list/delete/history/receipts and full Profile-content reads remain separate work.
Secret-bearing effective expansion still refuses with
`profile.secret-projections.unsupported` and null layouts. Assignment does not
verify cross-Profile document/slot/path compatibility or execute Provider effects.
