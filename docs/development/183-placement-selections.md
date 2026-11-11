# Exact Placement selections

Issue #183 closed administration contract (defined before implementation).

## Wire target and commands

`PlacementSelectionTarget` has four scalar fields: `kind`, `selector`,
`application_id`, `component_ref`. Exactly these combinations are accepted:
`named` + selector; `environment` alone; `application` + application_id;
`component` + application_id + component_ref. All unused strings must be empty.
Names follow their existing typed ID rules. Unknown kinds, extra target fields,
missing target, scope, reference or precondition are invalid context.

Apply and exact get carry Environment name and Provider alias. Apply carries
explicit Placement name and positive Int32 revision, plus exactly one of
`expected_absence=true` or a nonempty current UUID concurrency token. Component
apply alone requires explicit ApplicationVersion; it validates an exact accepted
ManifestRevision and records that reference as audit, never selection identity.
No null/empty version, inferred latest, force, automatic read/retry or deletion.

```text
habyctl placement selection apply <kind> --provider-alias <alias> --placement <name> --revision <positive-int> [--selector <name>] [--application <id>] [--component <ref> --application-version <version>] (--expected-absence | --expected-token <uuid>)
habyctl placement selection get <kind> --provider-alias <alias> [--selector <name>] [--application <id>] [--component <ref>]
```

Kinds are `named`, `environment`, `application`, `component`. Existing registry,
context, credentials and `-o json` apply. Get accepts no validation version.
Target options are closed by kind and rejected on impossible combinations.
The server repeats validation. Import creates no selection. A named selector
may choose a differently named Placement. Every choice references one whole
existing revision in the same Environment and alias.

CAS checks precede content equality. Stale-equal conflicts. A valid equal
reference preserves token, actor, timestamp and checked ManifestRevision even
when another explicit version is used for current Component validation. Actual
changes atomically replace reference, token and audit. Unsupported/corrupt
present data refuses without fallback. No Provider registration is required.

## Boundaries

Named explicit Workload placementRef takes precedence and refuses if missing;
otherwise Component > Application > Environment, isolated by Provider alias.
Logical component selections survive later versions but cannot transfer to a
renamed component. No overlay or partial merge is introduced. Selection metadata
changes no accepted Plan, desired/applied generation, operation or external object.
Durable input capture, freshness, stages and execution remain unimplemented.

## Operator rehearsal

Import the complete [Placement example](examples/181-placement.json), then select
it explicitly. The selected context supplies the Environment, which must exist.
Use an operator-admin credential configured through the existing registry/context
flow in [operator access](150-operator-access-tokens.md).

```powershell
habyctl placement import ./placement.json --provider-alias cluster
habyctl placement selection apply environment --provider-alias cluster --placement standard-runtime --revision 1 --expected-absence
habyctl placement selection apply named --selector production --provider-alias cluster --placement standard-runtime --revision 1 --expected-absence
habyctl placement selection apply application --application example --provider-alias cluster --placement standard-runtime --revision 1 --expected-absence
habyctl placement selection apply component --application example --component api --application-version 2 --provider-alias cluster --placement standard-runtime --revision 1 --expected-absence
$result = habyctl placement selection get environment --provider-alias cluster -o json | ConvertFrom-Json
# Import an explicitly numbered revision 2 before this conditional switch.
habyctl placement selection apply environment --provider-alias cluster --placement standard-runtime --revision 2 --expected-token $result.data.concurrencyToken
```

The Application must already be imported; deployment is unnecessary. Component
apply validates only the supplied exact ApplicationVersion. A later version may
reuse the logical assignment; an absent/renamed Component receives none of its
selection. Get requires neither version nor live Provider registration.

Both text and generated JSON report exact target/reference, EnvironmentRecordId,
concurrency token, actor, UTC change time and Component checked ManifestRevision
(ApplicationRecordId + local revision number). A named selector need not match
its selected Placement name. Scope and reference are explicit; there is no
latest/current/default revision inference. There is no selection manifest format.
Wire target strings not applicable to a kind must be empty; unknown target fields
refuse. Apply uses two presence-aware optional precondition fields so supplying
both is rejected even when expected absence is false. CLI rejects conflicting
precondition options before contacting the server and performs no hidden read/retry.

Machine outcomes are `created`, `updated`, `unchanged`, `found`, `notFound`,
`conflict`, `invalidContext`, `environmentNotFound`, `applicationNotFound`,
`applicationVersionNotFound`, `componentNotFound`, `revisionNotFound`. The first
four exit 0; typed refusals exit 1; local usage/transport retain exits 2/3.
A stale token conflicts even when requesting the winner's current reference.
After conflict, inspect exact get and make an explicit operator decision before
supplying its new token. A valid equal request returns the original audit/token,
including original checked revision, while still validating today's supplied
Component context. A lost response can be resolved by exact get; there is no
automatic retry that assumes a write succeeded.

## Persistence, concurrency and catalog

The additive `AddPlacementSelections` migration adds a Provider-neutral
`placement_selections` table with a unique Environment/alias/closed-target key,
revision identity, concurrency token and audit/context. Its composite foreign key
to `placement_revisions` enforces exact Environment/alias agreement and prevents
missing revision references. It adds an alternate principal key; it does not
rewrite immutable revision payloads or add declaration fields to storage.
No new reset, backfill, codec version, dependency, Provider-specific column,
workflow SQL, trigger, database-role split or Kubernetes SDK is introduced.
Existing current-alpha normalized-catalog requirements still apply.

EF's token predicate arbitrates real updates. Equal-content contenders issue a
conditional update retaining the same token; a concurrent replacement cannot
produce a false Unchanged result. Unique-key conflicts arbitrate first creation.
All successful responses reflect the committed exact reference, token and audit.
There are no added advisory locks. Cancellation or failure before commit rolls
back the whole write; after entering commit the store awaits the database outcome
without treating late caller cancellation as confirmed rollback.

Exact reads validate target, token, audit, Component checked context and referenced
adapter contract/content. Component audit validation reuses the active catalog
context/transaction connection and the existing ManifestRevision decoder; it does
not require an additional connection-pool slot. Corrupt/unsupported present data is unavailable, never
absence/inheritance, and is neither repaired nor overwritten. Host exceptions use
the fixed gRPC Internal detail `Placement selection operation failed.` No submitted
values, driver exceptions or secret material enter errors. Existing immutable
revision guards and deletion restrictions remain unchanged.

Application owns neutral contracts/orchestration; persistence owns EF I/O; Host
composes the adapter codec, store and administration explicitly. CLI uses generated
RPCs and generated JSON metadata. `PersistedPlacementInputs` is an adapter-owned
pure bridge from explicitly supplied stored revisions/selections plus exact
synthetic Provider resolutions into the unchanged planner. It is unregistered in
durable planning and confers no admission or freshness/execution authority.

## Evidence and remaining work

Tests cover all four generated RPC/CLI targets, authenticated refusal, imported
explicit versions in reverse order, missing/renamed Components, equal-context audit
preservation, stale-equal CAS, same-scope exact revision checks, independent-context
stored-value assertions, single-connection-pool Component create/update/equal/get,
synchronized create/update/equal/mixed races, precommit
rollback/cancellation, foreign-key failures, additive upgrade, restart and dump/restore.
The pure bridge proves named > Component > Application > Environment priority,
whole revision/settings, missing explicit-selector refusal and alias isolation.
Native AOT loopback tests run on Windows and Linux CI; the measured PostgreSQL
suite also exercises the published native CLI against the real Host. Final exact
SHA validation and retained failures are reported in the pull request.

Removal/inheritance reset, deletion, overlays, disable semantics, durable exact
Provider/Placement input capture, fingerprints/freshness, Commit/Execute admission,
stages, Kubernetes access/preflight/effects/readiness/publication/retirement and
load/corrective work remain open. Existing Resource/configuration/generation
behavior and Workload admission guards remain unchanged.
